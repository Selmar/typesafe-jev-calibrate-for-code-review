using System;
using System.Collections.Generic;
using Trecs;
using UnityEngine.Assertions;

namespace Delve
{
	// Authored compositions -> runtime archetypes + definition blobs. Phases, in order:
	// 1. DeclareUnkeyed / DeclareCollection — before world build: registers archetypes for every reachable definition.
	// 2. BuildTemplates — before world build.
	// 3. BuildBlobRegistry — after world build: publishes keyed definitions into DefinitionRegistry.
	public sealed class DefinitionCompiler
	{
		readonly struct DefinitionEntry
		{
			public readonly string Name;
			public readonly IReadOnlyList<ModuleConfig> Modules;
			public readonly IReadOnlyList<NestedDefinition> Nested;
			public readonly TemplateRegistry.RegisteredArchetype Archetype;

			public DefinitionEntry(string name, IReadOnlyList<ModuleConfig> modules, IReadOnlyList<NestedDefinition> nested, TemplateRegistry.RegisteredArchetype archetype)
			{
				Name = name;
				Modules = modules;
				Nested = nested;
				Archetype = archetype;
			}
		}

		static readonly IReadOnlyList<NestedDefinition> NoNested = Array.Empty<NestedDefinition>();

		readonly TemplateRegistry _registry = new();

		readonly List<DefinitionEntry> _allDefinitionEntries = new();

		// Published subset.
		readonly Dictionary<EntityDefKey, int> _byKey = new();

		// Dedups timeline nodes.
		readonly Dictionary<TimelineNodeConfig, EntityDefKey> _nodeKeys = new();

		readonly Dictionary<EntityCreateConfig, EntityDefKey> _inlineCreateKeys = new();

		readonly Dictionary<EntityDefinitionAsset, EntityDefKey> _assetKeys = new();

		int _generatedCounter;
		int _timelineCount;

		// Phase 1, before world build.
		internal void DeclareCollection(EntityDefinitionCollection collection)
		{
			Assert.IsNotNull(collection, "DeclareCollection: null collection");
			foreach (var asset in collection.Definitions)
			{
				Assert.IsNotNull(asset, $"'{collection.name}': empty definition entry");
				DeclareDefinitionAsset(asset);
			}
		}

		// Phase 1, before world build.
		internal TemplateRegistry.RegisteredArchetype DeclareUnkeyed(string nameHint, List<ModuleConfig> configs, IReadOnlyList<NestedDefinition> nested, out int entry)
		{
			entry = DeclareEntry(nameHint, configs, nested);
			return _allDefinitionEntries[entry].Archetype;
		}

		// Phase 2.
		public IReadOnlyList<Template> BuildTemplates() => _registry.BuildTemplates();

		void DeclareKeyed(EntityDefKey key, string nameHint, List<ModuleConfig> configs, IReadOnlyList<NestedDefinition> nested)
			=> _byKey[key] = DeclareEntry(nameHint, configs, nested);

		// Takes ownership of configs and the configs in it: augments the list, modifies the configs.
		// Returns entry index: module walk (before) and nested declare (after) append their own entries.
		int DeclareEntry(string nameHint, List<ModuleConfig> configs, IReadOnlyList<NestedDefinition> nested)
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
						DeclareTimeline(timeline, $"{nameHint} ▸ {config.Module}");
						break;
					case IEntityCreateModuleConfig create:
						create.Create.ResolvedDefinition = DeclareEntityCreate(create.Create, $"{nameHint} ▸ {config.Module}");
						break;
				}
				modules.Add(config.Module);
			}

			var archetype = _registry.Register(ComposedEntityDefinition.FromModules(nameHint, modules));
			int index = _allDefinitionEntries.Count;
			_allDefinitionEntries.Add(new DefinitionEntry(nameHint, configs, nested ?? NoNested, archetype));
			DeclareNested(nested);
			return index;
		}

		void DeclareAbilities(AbilityOwnerModuleConfig owner)
		{
			owner.ResolvedOrdered = DeclareDefinitionAssets(owner.Ordered);
			owner.ResolvedAuto = DeclareDefinitionAssets(owner.Auto);
		}

		// Null entries keep their slot with a default key.
		EntityDefKey[] DeclareDefinitionAssets(EntityDefinitionAsset[] assets)
		{
			var keys = new EntityDefKey[assets.Length];
			for (int i = 0; i < assets.Length; i++)
				if (assets[i] != null)
					keys[i] = DeclareDefinitionAsset(assets[i]);
			return keys;
		}

		EntityDefKey DeclareEntityCreate(EntityCreateConfig config, string context)
		{
			// Asset ref: reuses the asset's key.
			if (config.DefinitionAsset != null)
				return DeclareDefinitionAsset(config.DefinitionAsset);

			// Inline: generated key, deduped by config identity.
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

			// Copy: declare augments the list; the asset's serialized modules must stay untouched.
			var configs = new List<ModuleConfig>(definition.Modules);
			DeclareKeyed(definition.Id, asset.name, configs, definition.Nested);
			return definition.Id;
		}

		void DeclareNested(IReadOnlyList<NestedDefinition> nested)
		{
			if (nested == null)
				return;
			foreach (var n in nested)
				if (n.DefinitionAssetRef != null)
					DeclareDefinitionAsset(n.DefinitionAssetRef);
		}

		void DeclareTimeline(ActionTimelineModuleConfig timeline, string context)
		{
			_timelineCount++;
			for (int i = 0; i < timeline.Nodes.Count; i++)
			{
				var node = timeline.Nodes[i];
				Assert.IsNotNull(node, $"'{context}': empty node entry");
				node.ResolvedDefinition = DeclareTimelineNode(node, $"{context}[{i}]");
			}
		}

		EntityDefKey DeclareTimelineNode(TimelineNodeConfig node, string context)
		{
			if (_nodeKeys.TryGetValue(node, out var existing))
				return existing;

			var def = node.Definition;
			Assert.IsNotNull(def, $"'{context}': no definition (inline or asset)");

			var configs = new List<ModuleConfig>(def.Modules);
			ModuleClosure.Ensure(configs, EntityModules.ConditionTime, () => new ConditionTimeModuleConfig());

			Assert.IsFalse(ModuleClosure.IsPlaced(configs), $"'{context}': timeline node has a transform, but timeline nodes spawn without a transform anchor");

			var key = MintGeneratedKey();
			_nodeKeys[node] = key; // set before recursion — guards re-entry
			DeclareKeyed(key, node.DefinitionAsset != null ? $"{context} ▸ {node.DefinitionAsset.name}" : context, configs, def.Nested);
			return key;
		}

		EntityDefKey MintGeneratedKey() => EntityDefKey.Generated(_generatedCounter++);

		// By entry, null for none.
		public IReadOnlyList<LookSource> BuildLooks()
		{
			// A look shared by several definitions is walked once.
			var walked = new Dictionary<Look, LookSource>();
			var looks = new LookSource[_allDefinitionEntries.Count];
			for (int i = 0; i < _allDefinitionEntries.Count; i++)
				foreach (var config in _allDefinitionEntries[i].Modules)
				{
					if (config is not LookModuleConfig lookConfig || lookConfig.Look == null)
						continue;

					if (!walked.TryGetValue(lookConfig.Look, out var source))
						walked.Add(lookConfig.Look, source = lookConfig.Look.Walk());
					looks[i] = source;
				}

			return looks;
		}

		// Phase 3, after world build. Publishes every keyed entity definition into DefinitionRegistry.
		public void BuildBlobRegistry(DefinitionRegistry definitions)
		{
			ValidateNested();
			definitions.CreateDefinitionRegistry(_byKey.Count, _timelineCount);
			foreach (var kvp in _byKey)
			{
				var entry = _allDefinitionEntries[kvp.Value];
				definitions.AddEntityDefinition(kvp.Key, kvp.Value, entry.Name, entry.Modules, entry.Nested, entry.Archetype.TagSet);
			}
		}

		void ValidateNested()
		{
			foreach (var entry in _allDefinitionEntries)
			{
				bool parentIsPlaced = ModuleClosure.IsPlaced(entry.Modules);
				foreach (var n in entry.Nested)
				{
					var nd = n.DefinitionAssetRef;
					if (nd == null)
						continue;
					var def = nd.Definition;
					Assert.IsTrue(_byKey.ContainsKey(def.Id), $"'{entry.Name}': nested '{def.Name}' was not declared");
					Assert.IsTrue(parentIsPlaced || !ModuleClosure.IsPlaced(def.Modules), $"'{entry.Name}': nests placed '{def.Name}' but has no transform to anchor it");
				}
			}

			// We only need to validate keyed entries, because only keyed entries can be referenced (and thus be cyclic).
			var state = new Dictionary<EntityDefKey, byte>(_byKey.Count);
			foreach (var kvp in _byKey)
				DetectSpawnCycle(kvp.Key, state);
		}

		void DetectSpawnCycle(EntityDefKey key, Dictionary<EntityDefKey, byte> state)
		{
			const byte Visiting = 1, Done = 2;

			var entry = _allDefinitionEntries[_byKey[key]];
			state.TryGetValue(key, out var mark);
			Assert.IsTrue(mark != Visiting, $"definition spawn cycle through '{entry.Name}'");
			if (mark == Done)
				return;

			state[key] = Visiting;
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
			state[key] = Done;
		}
	}
}
