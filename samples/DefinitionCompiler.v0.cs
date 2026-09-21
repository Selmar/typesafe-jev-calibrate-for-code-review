using System;
using System.Collections.Generic;
using Trecs;
using UnityEngine.Assertions;

namespace Delve
{
	// Compiles authored compositions into runtime archetypes. Declare (before world build, templates
	// are builder-only) walks scene initializers and the collection, then follows every definition
	// reference transitively into one TemplateRegistry — reachability is what makes a definition
	// declared, so the collection only needs the roots nothing references (spawn-by-key from code or a
	// save). Every composition funnels through RegisterEntry, which recurses into the
	// two reference-carrying modules — AbilityOwner (each ability's definition) and
	// ActionTimeline (each node's def) — minting a generated EntityDefKey for every inline definition
	// and stamping it back onto the config so Bake resolves references by key. BuildBlobRegistry (after
	// build) bakes every keyed definition (asset + generated) into CDefinitionRegistry; each
	// ActionTimeline module bakes its TimelineBlob as a side-effect. Scene and runtime spawn share one
	// path: EntityInitializer bakes a transient per-instance blob and spawns it through
	// EntitySpawnUtility, same as spawn-by-key (see notes/entity-initializer-design.md).
	public sealed class DefinitionCompiler
	{
		readonly struct Entry
		{
			public readonly string Name;
			public readonly IReadOnlyList<ModuleConfig> Modules;
			public readonly IReadOnlyList<NestedDefinition> Nested;
			public readonly TemplateRegistry.RegisteredArchetype Archetype;

			public Entry(string name, IReadOnlyList<ModuleConfig> modules, IReadOnlyList<NestedDefinition> nested, TemplateRegistry.RegisteredArchetype archetype)
			{
				Name = name;
				Modules = modules;
				Nested = nested;
				Archetype = archetype;
			}
		}

		static readonly IReadOnlyList<NestedDefinition> NoNested = Array.Empty<NestedDefinition>();

		readonly TemplateRegistry _registry = new();

		// Every composition declared this run, keyed or not — scene compositions bake transient, so they
		// have no key but still carry a nested list to validate.
		readonly List<Entry> _entries = new();

		// The keyed subset, by index into _entries: what BuildBlobRegistry publishes and what a nested
		// or spawn reference resolves through.
		readonly Dictionary<EntityDefKey, int> _byKey = new();

		// Timeline node config -> its generated key. Dedups a shared composition declared more than
		// once (e.g. one definition asset used by several scene initializers) to one node def. Per-run
		// (new compiler each world), so it never resolves to a stale key from a prior run.
		readonly Dictionary<TimelineNodeConfig, EntityDefKey> _nodeKeys = new();

		// Inline spawn definition -> its generated key. Same per-run lifetime as _nodeKeys.
		readonly Dictionary<EntityCreateConfig, EntityDefKey> _inlineCreateKeys = new();

		// Definition asset -> its key, for assets already declared. Also the re-entry guard: a reference
		// cycle resolves to the key instead of recursing.
		readonly Dictionary<EntityDefinitionAsset, EntityDefKey> _assetKeys = new();

		int _generatedCounter;
		int _timelineCount;

		// Declare phase — before world construction. Scene compositions and every collection definition
		// register their archetypes (a definition gets a template whether or not a scene references it,
		// so any spawn-by-key has a group to resolve).
		internal void Declare(Initializer[] sceneInitializers, EntityDefinitionCollection collection = null)
		{
			foreach (var initializer in sceneInitializers)
				if (initializer is EntityInitializer entityInitializer)
					entityInitializer.DeclareTemplate(this);

			DeclareDefinitions(collection);
		}

		// Feeds the registered archetypes to the WorldBuilder; call once after Declare.
		public IReadOnlyList<Template> BuildTemplates() => _registry.BuildTemplates();

		// Keyless entry point, for a scene composition that bakes transient.
		internal TemplateRegistry.RegisteredArchetype RegisterComposition(string nameHint, List<ModuleConfig> configs, IReadOnlyList<NestedDefinition> nested, out int entry)
		{
			entry = RegisterEntry(nameHint, configs, nested);
			return _entries[entry].Archetype;
		}

		// Keyed entry point: same registration, plus the key -> entry lookup spawn resolves through.
		void DeclareKeyed(EntityDefKey key, string nameHint, List<ModuleConfig> configs, IReadOnlyList<NestedDefinition> nested)
			=> _byKey[key] = RegisterEntry(nameHint, configs, nested);

		// Every composition funnels through here — one place that walks the modules, records the entry
		// and declares the nested defs, so no declare site can register a composition and forget half of
		// it. AbilityOwner and ActionTimeline carry references to other definitions — declare each
		// (minting a generated key for inline ones) and stamp the key back onto the config for Bake to
		// read. Augments configs in place, so the caller's list is the effective composition — the same
		// one that must reach Bake, or the template carries components the blob has no payload for.
		// Returns the entry index: the module walk and the nested declare both append entries of their
		// own, so neither end of the list identifies this one. The caller hands over configs — the entry
		// keeps it, so it must not be pooled or reused.
		int RegisterEntry(string nameHint, List<ModuleConfig> configs, IReadOnlyList<NestedDefinition> nested)
		{
			ModuleClosure.Apply(configs);

			using var pooled = UnityEngine.Pool.ListPool<EntityModule>.Get(out var modules);
			foreach (var config in configs)
			{
				Assert.IsNotNull(config, $"'{nameHint}': empty module config entry");
				switch (config)
				{
					case AbilityOwnerModuleConfig owner:
						DeclareAbilities(owner);
						break;
					case ActionTimelineModuleConfig timeline:
						DeclareTimelineNodes(timeline, $"{nameHint} ▸ {config.Module}");
						break;
					case IEntityCreateModuleConfig create:
						create.Create.ResolvedDefinition = DeclareEntityCreate(create.Create, $"{nameHint} ▸ {config.Module}");
						break;
				}
				modules.Add(config.Module);
			}

			var archetype = _registry.Register(ComposedEntityDefinition.FromModules(nameHint, modules));
			int index = _entries.Count;
			_entries.Add(new Entry(nameHint, configs, nested ?? NoNested, archetype));
			DeclareNested(nested);
			return index;
		}

		// An ability is an ordinary definition asset (the Ability contract is what marks it castable), so
		// it declares through the shared path and shares the asset's own key with every other reference
		// to it.
		void DeclareAbilities(AbilityOwnerModuleConfig owner)
		{
			owner.ResolvedOrdered = Declare(owner.Ordered);
			owner.ResolvedAuto = Declare(owner.Auto);

			EntityDefKey[] Declare(EntityDefinitionAsset[] list)
			{
				var keys = new EntityDefKey[list.Length];
				for (int i = 0; i < list.Length; i++)
					if (list[i] != null)
						keys[i] = DeclareDefinitionAsset(list[i]);
				return keys;
			}
		}

		// An external ref reuses the asset's own key: unlike a timeline node there is no per-site override,
		// so minting a fresh key would duplicate the archetype and its blob for nothing. Inline
		// definitions get a generated key, deduped by config identity so one config declares once —
		// the first declare site's path names it, since one shared config has no single owner.
		EntityDefKey DeclareEntityCreate(EntityCreateConfig config, string context)
		{
			if (config.DefinitionAsset != null)
				return DeclareDefinitionAsset(config.DefinitionAsset);

			if (_inlineCreateKeys.TryGetValue(config, out var existing))
				return existing;

			var def = config.InlineDefinition;
			Assert.IsNotNull(def, $"'{context}': no definition (inline or asset)");
			if (def.Modules == null || def.Modules.Count == 0)
				return default; // Validate reports it; nothing to declare

			var configs = new List<ModuleConfig>(def.Modules);
			var key = MintGeneratedKey();
			_inlineCreateKeys[config] = key; // set before recursion — guards a definition spawning itself
			DeclareKeyed(key, context, configs, def.Nested);
			return key;
		}

		// A referenced definition asset declares itself, so reachability is the membership rule — nothing
		// has to be listed anywhere for a reference to resolve. The collection only adds the roots
		// nothing references statically (spawn-by-key from code or a save).
		internal EntityDefKey DeclareDefinitionAsset(EntityDefinitionAsset asset)
		{
			Assert.IsNotNull(asset, "declare definition: null asset");
			if (_assetKeys.TryGetValue(asset, out var existing))
				return existing;

			var definition = asset.Definition;
			Assert.IsNotNull(definition, $"'{asset.name}': empty definition");
			Assert.IsFalse(definition.Id.IsNone, $"'{asset.name}': definition without id");
			Assert.IsFalse(definition.Id.IsGenerated, $"'{asset.name}': asset id in the compiler-minted range");
			Assert.IsFalse(_byKey.ContainsKey(definition.Id), $"'{asset.name}': duplicate definition id {definition.Id}");

			_assetKeys[asset] = definition.Id; // set before recursion — guards a definition reaching itself

			// Copy: the declare augments the list, and the asset's own is serialized data.
			var configs = new List<ModuleConfig>(definition.Modules);
			DeclareKeyed(definition.Id, asset.name, configs, definition.Nested);
			return definition.Id;
		}

		// Nested defs spawn by key, so each must be declared — including from a scene initializer, whose
		// transient blob bakes its definition's nested list without declaring the definition itself.
		void DeclareNested(IReadOnlyList<NestedDefinition> nested)
		{
			if (nested == null)
				return;
			foreach (var n in nested)
				if (n.DefinitionAssetRef != null)
					DeclareDefinitionAsset(n.DefinitionAssetRef);
		}

		void DeclareTimelineNodes(ActionTimelineModuleConfig timeline, string context)
		{
			_timelineCount++;
			for (int i = 0; i < timeline.Nodes.Count; i++)
			{
				var node = timeline.Nodes[i];
				Assert.IsNotNull(node, $"'{context}': empty node entry");
				node.ResolvedDefinition = DeclareTimelineNode(node, $"{context}[{i}]");
			}
		}

		// A node's def is an inline definition wrapped as a time-gated reaction: its modules plus an
		// ensured ConditionTime (the framework's time gate; ActionTimelineSystem overrides the window
		// per cast — the reaction core follows from it via ModuleClosure). Always a fresh generated key
		// — even an external ref is copied here so the shared collection def keeps its ungated shape
		// (per-node overrides come later).
		EntityDefKey DeclareTimelineNode(TimelineNodeConfig node, string context)
		{
			if (_nodeKeys.TryGetValue(node, out var existing))
				return existing;

			var def = node.Definition;
			Assert.IsNotNull(def, $"'{context}': no definition (inline or asset)");

			var configs = new List<ModuleConfig>(def.Modules);
			ModuleClosure.Ensure(configs, EntityModules.ConditionTime, () => new ConditionTimeModuleConfig());

			// A node is spawned with no placement (ActionTimelineSystem has no anchor to offer), so a body
			// would fail its transform check on every cast. The node's own actions place what they spawn.
			Assert.IsFalse(HasTransform(configs), $"'{context}': timeline node is placed, but a node is spawned without a placement");

			var key = MintGeneratedKey();
			_nodeKeys[node] = key; // set before recursion — guards re-entry
			DeclareKeyed(key, node.DefinitionAsset != null ? $"{context} ▸ {node.DefinitionAsset.name}" : context, configs, def.Nested);
			return key;
		}

		void DeclareDefinitions(EntityDefinitionCollection collection)
		{
			if (collection == null)
				return;

			foreach (var asset in collection.Definitions)
			{
				Assert.IsNotNull(asset, $"'{collection.name}': empty definition entry");
				DeclareDefinitionAsset(asset);
			}
		}

		EntityDefKey MintGeneratedKey() => EntityDefKey.Generated(_generatedCounter++);

		// By entry, null for none. A look shared by several definitions is walked once.
		public IReadOnlyList<LookSource> BuildLooks()
		{
			var walked = new Dictionary<Look, LookSource>();
			var looks = new LookSource[_entries.Count];
			for (int i = 0; i < _entries.Count; i++)
				foreach (var config in _entries[i].Modules)
				{
					if (config is not LookModuleConfig lookConfig || lookConfig.Look == null)
						continue;

					if (!walked.TryGetValue(lookConfig.Look, out var source))
						walked.Add(lookConfig.Look, source = lookConfig.Look.Walk());
					looks[i] = source;
				}

			return looks;
		}

		// Publishes every declared definition (collection + generated inline) into the
		// CDefinitionRegistry global, reusing the Declare archetypes. Each ActionTimeline module bakes
		// its TimelineBlob as a side-effect (into _timelines). The global's create/add/dispose live on
		// DefinitionRegistry (owns the accessor + world-lifetime teardown); this only drives the
		// iteration over the keys it holds. Runs at init after the world is built.
		public void BuildBlobRegistry(DefinitionRegistry definitions)
		{
			ValidateNested();
			definitions.CreateDefinitionRegistry(_byKey.Count, _timelineCount);
			foreach (var kvp in _byKey)
			{
				var entry = _entries[kvp.Value];
				definitions.AddEntityDefinition(kvp.Key, kvp.Value, entry.Name, entry.Modules, entry.Nested, entry.Archetype.TagSet);
			}
		}

		// Two checks over the declared graph. A nested def that needs a transform requires the parent to
		// have one too — the nested def inherits the parent transform, spawn has nothing else to anchor it
		// (transform-as-override is deferred). Over every entry, not just the keyed ones: a scene
		// composition has no key but nests by key like any other parent.
		// Then the spawn-recursion cycle check below.
		void ValidateNested()
		{
			foreach (var entry in _entries)
			{
				bool parentIsPlaced = HasTransform(entry.Modules);
				foreach (var n in entry.Nested)
				{
					var nd = n.DefinitionAssetRef;
					if (nd == null)
						continue;
					var def = nd.Definition;
					Assert.IsTrue(_byKey.ContainsKey(def.Id), $"'{entry.Name}': nested '{def.Name}' was not declared");
					Assert.IsTrue(parentIsPlaced || !HasTransform(def.Modules), $"'{entry.Name}': nests placed '{def.Name}' but has no transform to anchor it");
				}
			}

			// One spawn recurses into nested defs and abilities, so a cycle there would recurse forever —
			// reject at blob-build time. Timeline nodes are not on this graph: they spawn later, through
			// the request queue, so a loop among them is a runaway rather than a stack overflow.
			// Keyed entries only: a scene composition is never referenced, so it can't be on a cycle.
			var state = new Dictionary<EntityDefKey, byte>(_byKey.Count);
			foreach (var kvp in _byKey)
				DetectSpawnCycle(kvp.Key, state);
		}

		// DFS colouring: revisiting an in-stack (grey = 1) node is a back edge ⇒ cycle; 2 = done.
		void DetectSpawnCycle(EntityDefKey key, Dictionary<EntityDefKey, byte> state)
		{
			var entry = _entries[_byKey[key]];
			state.TryGetValue(key, out var mark);
			Assert.IsTrue(mark != 1, $"definition spawn cycle through '{entry.Name}'");
			if (mark == 2)
				return;

			state[key] = 1;
			foreach (var nested in entry.Nested)
			{
				var n = nested.DefinitionAssetRef?.Definition;
				if (n != null && _byKey.ContainsKey(n.Id))
					DetectSpawnCycle(n.Id, state);
			}
			foreach (var config in entry.Modules)
			{
				if (config is not AbilityOwnerModuleConfig owner)
					continue;
				for (int list = 0; list < 2; list++)
					foreach (var asset in list == 0 ? owner.Ordered : owner.Auto)
					{
						var a = asset?.Definition;
						if (a != null && _byKey.ContainsKey(a.Id))
							DetectSpawnCycle(a.Id, state);
					}
			}
			state[key] = 2;
		}

		// Needs a spawn placement: the Transform module brings CPosition/CRotation. Implies, not Has —
		// Body pulls it in through the closure, and the editor validations read the authored set.
		internal static bool HasTransform(IReadOnlyList<ModuleConfig> modules)
			=> ModuleClosure.Implies(modules, EntityModules.Transform);

		// Proxy for "can hold attributes/resources": the Stats module is what brings them.
		internal static bool HasStats(IReadOnlyList<ModuleConfig> modules)
			=> Has(modules, EntityModules.Stats);

		internal static bool Has(IReadOnlyList<ModuleConfig> modules, EntityModule module)
		{
			if (modules == null)
				return false;
			foreach (var config in modules)
				if (config != null && config.Module == module)
					return true;
			return false;
		}
	}
}
