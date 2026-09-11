using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GHPC.World.Terrains;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// Stops the game's own terrain snapshot from killing the process when a mission is (re)started.
    ///
    /// Reaching the terrain scene fires GameState.TerrainSceneLoaded, and
    /// TerrainModificationManager.FinalInitialization answers it by calling CloneTerrainData, which walks
    /// every Terrain in the scene and copies its holes, heights, detail layers and trees into the save
    /// data that SceneController.LoadBaseScene restores before it unloads the scene. The detail-layer
    /// copy - TerrainData.GetDetailLayer - is a native call into the terrain's detail database, and on
    /// the second load of a terrain scene that database is no longer there: the call writes through a
    /// null pointer (0xC0000005 writing to 0x20, reproduced on two consecutive restarts, faulting
    /// instruction identical both times) and takes the whole process down before any managed frame - the
    /// mod's or the game's - can do anything about it.
    ///
    /// The snapshot is read back only by RestoreOriginalTerrainData, so this prefix rebuilds the very
    /// same save data - holes, heights and trees exactly as the game captured them - and leaves out only
    /// the detail layers, which is precisely the part that cannot be read back safely. Nothing else
    /// changes: FinalInitialization still applies the scene's TerrainModifiers (cutting holes, setting
    /// heights, clearing detail) right after this returns.
    /// </summary>
    [HarmonyPatch(typeof(TerrainModificationManager), "CloneTerrainData")]
    internal static class TerrainSnapshotPatch
    {
        private static readonly Type SaveDataType =
            AccessTools.Inner(typeof(TerrainModificationManager), "TerrainSaveData");

        private static readonly FieldInfo SaveDataListField =
            AccessTools.Field(typeof(TerrainModificationManager), "_terrainHoleData");

        private static readonly ConstructorInfo SaveDataCtor = SaveDataType == null
            ? null
            : SaveDataType
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(c => c.GetParameters().Length == 5);

        private static bool Prefix(TerrainModificationManager __instance, Terrain[] terrains)
        {
            if (SaveDataListField == null || SaveDataType == null || SaveDataCtor == null)
            {
                Log.Error("terrain snapshot: the game's TerrainSaveData shape changed, so the mission-restart " +
                          "terrain crash workaround is inactive (TerrainData.GetDetailLayer can kill the process).");
                return true;
            }

            IList saved = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(SaveDataType));
            int unusable = 0;
            if (terrains != null)
            {
                foreach (Terrain terrain in terrains)
                {
                    if (terrain == null || terrain.terrainData == null)
                    {
                        continue;
                    }
                    TerrainData data = terrain.terrainData;
                    if (!HasUsableDetailData(data))
                    {
                        unusable++;
                    }
                    try
                    {
                        saved.Add(SaveDataCtor.Invoke(new object[]
                        {
                            terrain,
                            data.GetHoles(0, 0, data.holesResolution, data.holesResolution),
                            data.GetHeights(0, 0, data.heightmapResolution, data.heightmapResolution),
                            new int[0][,],
                            data.treeInstances,
                        }));
                    }
                    catch (Exception e)
                    {
                        Log.Error("terrain snapshot: skipped '" + data.name + "' (" + e.Message + ")");
                    }
                }
            }

            SaveDataListField.SetValue(__instance, saved);
            Log.Info("terrain snapshot: saved " + saved.Count + " terrain(s) without their detail layers; " +
                     unusable + " of them report detail prototypes but no detail data, which is the state " +
                     "TerrainData.GetDetailLayer crashes the process on.");
            return false;
        }

        /// <summary>
        /// A terrain whose detail prototypes exist while its detail resolution is zero hands
        /// GetDetailLayer an unallocated detail database - the exact call that faults. Reading these
        /// three properties is safe (the game itself reads them right before it crashes).
        /// </summary>
        internal static bool HasUsableDetailData(TerrainData data)
        {
            if (data == null)
            {
                return false;
            }
            return data.detailPrototypes.Length == 0 || (data.detailWidth > 0 && data.detailHeight > 0);
        }
    }

    /// <summary>
    /// The same fault through the other door: FinalInitialization also runs ClearDetails for every
    /// TerrainModifier of the scene, and that loop is driven by detailPrototypes.Length as well, so a
    /// terrain with prototypes but no detail data would fault in exactly the same native call.
    ///
    /// This prefix only steps in when one of the terrains under the modifier is in that state - then the
    /// clearing cannot work anyway and dying is the only other option. For healthy terrain the game's
    /// own code runs untouched.
    /// </summary>
    [HarmonyPatch(typeof(TerrainModificationManager), "ClearDetails")]
    internal static class TerrainClearDetailsPatch
    {
        private static bool Prefix(TerrainModificationManager __instance, TerrainModifier terrainModifier)
        {
            if (terrainModifier == null)
            {
                return true;
            }

            TerrainModificationManager.TerrainDataIndexLimits[] limits;
            try
            {
                limits = __instance.GetTerrainDataIndexLimits(terrainModifier);
            }
            catch (Exception)
            {
                return true;
            }

            foreach (TerrainModificationManager.TerrainDataIndexLimits limit in limits)
            {
                if (limit != null && limit.Terrain != null && !TerrainSnapshotPatch.HasUsableDetailData(limit.Terrain.terrainData))
                {
                    Log.Warn("terrain detail: skipped clearing the detail layers of '" + terrainModifier.name +
                             "' because its terrain reports detail prototypes but no detail data " +
                             "(TerrainData.GetDetailLayer would kill the process).");
                    return false;
                }
            }
            return true;
        }
    }
}
