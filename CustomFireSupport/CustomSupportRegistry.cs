using System;
using System.Collections.Generic;
using GHPC;
using GHPC.Event;
using GHPC.Player;
using GHPC.State;
using GHPC.UI;
using GHPC.UI.Map;
using GHPC.Weaponry.CAS;
using GHPC.Weaponry.Interfaces;
using GHPC.Weapons.Artillery;
using GHPC.World;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CustomFireSupport
{
    /// <summary>
    /// Live state for one mission: the built slots, the objects injected into the game's managers and
    /// the buttons added to the map panel. All patch helpers live here so the Harmony patch classes stay
    /// tiny.
    ///
    /// Lifecycle:
    ///   MapController.InitControlState prefix  -> PrepareMission()  (build + inject, before vanilla
    ///                                             generates its own buttons / subscribes map clicks)
    ///   MelonMod.OnUpdate, once IsInitialized  -> CreateButtonsIfReady()
    ///   scene unload / next mission / hotkey   -> Teardown() + rebuild
    /// </summary>
    internal static class CustomSupportRegistry
    {
        private sealed class BuiltSlot
        {
            internal SlotConfig Config;
            internal IMapSupportInfo Info;
            /// <summary>Button label; differs from Config.DisplayName when one CAS slot split into several sorties.</summary>
            internal string DisplayName;
            internal GameObject Button;
            internal int ButtonAttempts;
            internal int NextButtonAttemptFrame;
        }

        private static readonly List<ArtillerySlot> _artillery = new List<ArtillerySlot>();
        private static readonly List<CasSlot> _cas = new List<CasSlot>();
        private static readonly List<BuiltSlot> _built = new List<BuiltSlot>();
        private static readonly List<GameObject> _buttons = new List<GameObject>();

        /// <summary>
        /// Frame in which each CAS slot last re-rolled its airframe. A single map click reaches
        /// TryCallCAS once per leaked subscriber, so this is what keeps one click to ONE draw (see
        /// TryRerollAirframeForCall). Cleared with the slots on a scene change.
        /// </summary>
        private static readonly Dictionary<int, int> _rerolledFrame = new Dictionary<int, int>();

        private static GlobalConfig _global = new GlobalConfig();
        private static Faction _playerFaction = Faction.Neutral;
        private static FireMissionManager _fireManager;
        private static CasSupportManager _casManager;
        /// <summary>True when _casManager was created by the mod because the mission had none.</summary>
        private static bool _createdCasManager;
        /// <summary>False until the created manager's deploy point has been placed from a live player unit.</summary>
        private static bool _casDeployPlaced;
        /// <summary>
        /// Frames to wait for MapController.IsInitialized before creating the buttons anyway
        /// (10 s at 60 fps). Only a safety net for a map whose init coroutine never completes.
        /// </summary>
        private const int ButtonCreationGraceFrames = 600;

        private static bool _pendingButtons;
        private static bool _buttonsCreated;
        private static int _buttonWaitFrames;
        private static bool _hideVanillaThisMission;
        private static bool _playerCasCall;
        private static bool _markNextSortie;
        private static MapController _buttonMap;
        private static MapFireSupportPanel _buttonPanel;
        private static bool _pendingButtonLayout;
        private static int _layoutAttempts;
        private static int _nextLayoutAttemptFrame;
        private const int MaxButtonAttempts = 3;
        private const int ButtonRetryFrames = 30;

        /// <summary>
        /// True once the slots of the current mission have been built. PrepareMission runs from the
        /// MapController.InitControlState prefix, and the game creates a MapController again every time the
        /// map is opened in flight - without this flag the whole harvest (and its object scan) would re-run
        /// mid-mission, which is what crashed the game in GameObject.get_scene.
        /// </summary>
        private static bool _preparedThisMission;

        // Private game members reached through reflection (the game runs the original, non-publicized
        // assembly, so direct access to these would fail at runtime).
        private static readonly AccessTools.FieldRef<MapFireSupportPanel, GameObject> ButtonPrefabRef =
            AccessTools.FieldRefAccess<MapFireSupportPanel, GameObject>("_buttonPrefab");
        private static readonly AccessTools.FieldRef<MapFireSupportPanel, object> SupportIconMappingRef =
            AccessTools.FieldRefAccess<MapFireSupportPanel, object>("_supportIconMapping");
        private static readonly AccessTools.FieldRef<MapFireSupportPanel, List<Button>> PanelButtonsRef =
            AccessTools.FieldRefAccess<MapFireSupportPanel, List<Button>>("_buttons");
        private static readonly AccessTools.FieldRef<MapFireSupportPanel, ScrollRect> PanelScrollRef =
            AccessTools.FieldRefAccess<MapFireSupportPanel, ScrollRect>("_scrollRect");
        private static readonly Action<MapFireSupportPanel> UpdateScrollButtons =
            AccessTools.MethodDelegate<Action<MapFireSupportPanel>>(AccessTools.Method(typeof(MapFireSupportPanel), "OnScrollComplete"));
        private static readonly AccessTools.FieldRef<MapController, FireMissionManager> MapFireManagerRef =
            AccessTools.FieldRefAccess<MapController, FireMissionManager>("_fireMissionManager");
        private static readonly AccessTools.FieldRef<MapController, CasSupportManager> MapCasManagerRef =
            AccessTools.FieldRefAccess<MapController, CasSupportManager>("_casSupportManager");
        private static readonly AccessTools.FieldRef<CasSupportManager, Vector2> BlueDeployPointRef =
            AccessTools.FieldRefAccess<CasSupportManager, Vector2>("blueDeployPoint");
        private static readonly AccessTools.FieldRef<CasSupportManager, Vector2> RedDeployPointRef =
            AccessTools.FieldRefAccess<CasSupportManager, Vector2>("redDeployPoint");

        internal static GlobalConfig Global
        {
            get { return _global; }
        }

        internal static bool HideVanillaThisMission
        {
            get { return _hideVanillaThisMission; }
        }

        // ------------------------------------------------------------------
        // Mission preparation
        // ------------------------------------------------------------------

        /// <summary>
        /// Called from the MapController.InitControlState prefix, i.e. before vanilla decides which
        /// support buttons to create and before it subscribes its map-click handlers.
        ///
        /// Runs ONCE per mission: the game calls InitControlState every time a MapController is created, and
        /// opening the map in flight creates one. Re-running the preparation there would re-harvest every
        /// battery and re-scan every loaded object mid-mission - wasteful, and dangerous, because that scan
        /// is what crashed the game in GameObject.get_scene (see FireSupportTemplates.FindLoadedPrefab).
        /// The slots are already built, so later calls only queue replacement map UI;
        /// CustomSupportRegistry.ResetForScene clears the flag for the next mission.
        /// </summary>
        internal static void PrepareMission(MapController mapController)
        {
            if (_preparedThisMission)
            {
                QueueButtonsForMap(mapController);
                return;
            }
            _preparedThisMission = true;

            Teardown(true);

            try
            {
                // Reads the [CustomFireSupport] section straight from MelonPreferences.cfg (see
                // CfgSectionParser): no MelonLoader entry layer, which was observed to rewrite values
                // (30 -> 0.0, 0.8 -> 0.3). The config is fixed for the mission - the slots are built here
                // and only rebuilt on the next mission start.
                ConfigSchema.Refresh();
                _global = ConfigSchema.ReadGlobal();
                // Also clear when support is disabled, including first-load bootstrap paths.
                CasAttackLibrary.Reset();
                CustomSlotBuilder.ResetLoadouts();
                CasPayloadFactory.ResetForMission();
                if (!_global.Enabled)
                {
                    return;
                }

                _playerFaction = ResolvePlayerFaction();
                // Pull the configured addressable CAS assets into memory if the first scene load
                // (menu) has not already done so - they then feed the donor scan and the library
                // below even when this mission offers no CAS of its own.
                CasPrewarmer.EnsurePrewarmed();
                CasBundleMaterialRepair.RefreshForScene();

                _fireManager = EnsureFireMissionManager(mapController);
                _casManager = EnsureCasSupportManager(mapController);

                SlotConfig[] slots = ConfigSchema.ReadSlots();
                bool needsAmmo = false;
                bool needsCas = false;
                for (int i = 0; i < slots.Length; i++)
                {
                    if (!slots[i].Enabled)
                    {
                        continue;
                    }
                    if (slots[i].IsCas)
                    {
                        needsCas = true;
                    }
                    else
                    {
                        needsAmmo = true;
                    }
                }

                Dictionary<MunitionKind, List<AmmoTemplate>> ammo = needsAmmo
                    ? FireSupportTemplates.HarvestAmmo(_fireManager, _playerFaction)
                    : new Dictionary<MunitionKind, List<AmmoTemplate>>();
                List<CasTemplate> casTemplates = needsCas
                    ? FireSupportTemplates.HarvestCas(_casManager, _playerFaction)
                    : new List<CasTemplate>();

                for (int i = 0; i < slots.Length; i++)
                {
                    SlotConfig config = slots[i];
                    if (!config.Enabled)
                    {
                        continue;
                    }
                    try
                    {
                        BuildSlot(config, ammo, casTemplates);
                    }
                    catch (Exception)
                    {
                    }
                }

                if (_built.Count == 0)
                {
                    _hideVanillaThisMission = false;
                    return;
                }

                EnsureCooldownManager();
                _hideVanillaThisMission = _global.HideVanillaFireSupport;
                CorrectPlayerFaction();
                InjectArtillery();
                InjectAirframes();
                QueueButtonsForMap(mapController);

            }
            catch (Exception)
            {
                _hideVanillaThisMission = false;
            }
        }

        private static void BuildSlot(SlotConfig config, Dictionary<MunitionKind, List<AmmoTemplate>> ammo, List<CasTemplate> casTemplates)
        {
            if (config.Kind == SlotKind.ArtilleryIllumination && _global.IlluminationOnlyAtNight && !IsNight())
            {
                return;
            }

            if (config.Kind == SlotKind.ArtillerySmoke && _global.SmokeOnlyDuringDay && IsNight())
            {
                return;
            }

            if (config.IsCas)
            {
                CasSlot slot = CustomSlotBuilder.BuildCas(config, casTemplates, _playerFaction);
                if (slot == null)
                {
                    return;
                }
                _cas.Add(slot);
                _built.Add(new BuiltSlot { Config = config, Info = slot.Airframe, DisplayName = config.DisplayName });
                return;
            }

            ArtillerySlot artillerySlot = CustomSlotBuilder.BuildArtillery(config, ammo, _playerFaction);
            if (artillerySlot == null)
            {
                return;
            }
            _artillery.Add(artillerySlot);
            _built.Add(new BuiltSlot { Config = config, Info = artillerySlot.Battery });
        }

        // ------------------------------------------------------------------
        // Injection
        // ------------------------------------------------------------------

        private static void InjectArtillery()
        {
            if (_fireManager == null || _artillery.Count == 0)
            {
                return;
            }

            bool red = _playerFaction == Faction.Red;
            ArtilleryBattery[] existing = red ? _fireManager.RedArtilleryBatteries : _fireManager.BlueArtilleryBatteries;
            ArtilleryBattery[] combined = new ArtilleryBattery[(existing == null ? 0 : existing.Length) + _artillery.Count];
            int index = 0;
            if (existing != null)
            {
                for (int i = 0; i < existing.Length; i++)
                {
                    combined[index++] = existing[i];
                }
            }
            for (int i = 0; i < _artillery.Count; i++)
            {
                combined[index++] = _artillery[i].Battery;
            }

            if (red)
            {
                _fireManager.RedArtilleryBatteries = combined;
            }
            else
            {
                _fireManager.BlueArtilleryBatteries = combined;
            }
        }

        private static void InjectAirframes()
        {
            if (_casManager == null || _cas.Count == 0)
            {
                return;
            }

            CorrectPlayerFaction();

            bool red = _playerFaction == Faction.Red;
            CasAirframeUnit[] existing = red ? _casManager.RedCasAirframes : _casManager.BlueCasAirframes;
            int vanilla = existing == null ? 0 : existing.Length;

            // The custom airframes take over the LOWEST indices instead of being appended.
            //
            // Nothing in the game remembers which button was clicked. Three separate places
            // independently pick "the first one that fits":
            //   MapFireSupportPanel.AddButton            - merges every CASSupport entry into the
            //                                              single existing CAS button, in call order
            //   MapFireSupportPanel.ActiveSupportInfo    - SupportInfos.FirstOrDefault(RemainingMissions > 0)
            //   CasSupportManager.SendCasSupport         - MapController.TryCallCAS passes no index,
            //                                              so this scans for the first IsReady entry
            // Appending the custom airframes therefore loses to the mission's own airframes on all
            // three counts, no matter what the config says. Taking the lowest indices flips all three.
            //
            // The array keeps its original length (and every index above the custom block keeps its
            // original airframe), so scripted CAS events that pass a serialized index stay valid.
            int taken = _cas.Count;
            CasAirframeUnit[] combined = new CasAirframeUnit[Math.Max(vanilla, taken)];
            for (int i = 0; i < taken; i++)
            {
                combined[i] = _cas[i].Airframe;
            }
            for (int i = taken; i < combined.Length; i++)
            {
                combined[i] = existing[i];
            }


            if (red)
            {
                _casManager.RedCasAirframes = combined;
            }
            else
            {
                _casManager.BlueCasAirframes = combined;
            }

            // Refresh the manager's snapshot of the player faction's aircraft, otherwise
            // CheckNextAvailableAirframe() would still look at the pre-injection array.
            try
            {
                _casManager.SetAirframes();
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------
        // Button creation
        // ------------------------------------------------------------------

        /// <summary>Called every frame from the MelonMod; creates the buttons once the map is ready.</summary>
        internal static void Tick()
        {
            EnsureCreatedCasDeployPoints();
            if (_preparedThisMission && MapController.Instance != null)
                QueueButtonsForMap(MapController.Instance);
            CreateButtonsIfReady();
        }

        private static void QueueButtonsForMap(MapController map)
        {
            if (map == null || !_global.Enabled || _built.Count == 0) return;
            MapFireSupportPanel panel = map.FireSupportPanel;
            if (ReferenceEquals(_buttonMap, map) && ReferenceEquals(_buttonPanel, panel))
            {
                for (int i = 0; i < _built.Count; i++)
                {
                    BuiltSlot slot = _built[i];
                    if (!ReferenceEquals(slot.Button, null) && slot.Button == null)
                    {
                        _buttons.Remove(slot.Button);
                        slot.Button = null;
                        slot.ButtonAttempts = 0;
                        slot.NextButtonAttemptFrame = 0;
                        _pendingButtons = true;
                        _buttonsCreated = false;
                    }
                }
                return;
            }

            // Only replace the UI. Sorties, missions remaining and their loadouts stay intact.
            ClearButtons();
            _buttonMap = map;
            _buttonPanel = panel;
            _buttonWaitFrames = 0;
            _buttonsCreated = false;
            _pendingButtons = true;
        }

        private static void ClearButtons()
        {
            List<Button> panelButtons = _buttonPanel != null ? PanelButtonsRef(_buttonPanel) : null;
            for (int i = 0; i < _buttons.Count; i++)
            {
                GameObject instance = _buttons[i];
                if (instance == null) continue;
                Button button = instance.GetComponent<Button>();
                if (panelButtons != null && button != null) panelButtons.Remove(button);
                instance.SetActive(false);
                instance.transform.SetParent(null, false);
                UnityEngine.Object.Destroy(instance);
            }
            _buttons.Clear();
            for (int i = 0; i < _built.Count; i++)
            {
                _built[i].Button = null;
                _built[i].ButtonAttempts = 0;
                _built[i].NextButtonAttemptFrame = 0;
            }
            _pendingButtonLayout = false;
            _layoutAttempts = 0;
            _nextLayoutAttemptFrame = 0;
        }

        internal static void CreateButtonsIfReady()
        {
            if (!_pendingButtons || _buttonsCreated)
            {
                return;
            }

            MapController map = MapController.Instance;
            if (map == null)
            {
                return;
            }

            if (!map.IsInitialized)
            {
                // The map finishes itself in a coroutine. If that coroutine aborts (an exception in
                // its second half, a mission whose managers are unusual, ...), IsInitialized never
                // flips and the custom slots would stay invisible forever. Give it a grace period,
                // then build the buttons regardless - the panel itself is already live by then.
                _buttonWaitFrames++;
                if (_buttonWaitFrames < ButtonCreationGraceFrames)
                {
                    return;
                }
            }

            MapFireSupportPanel panel = map.FireSupportPanel;
            ScrollRect scroll = panel != null ? PanelScrollRef(panel) : null;
            if (scroll == null || scroll.content == null)
            {
                // The panel may become available later; do not consume any slot's retry budget.
                return;
            }

            bool waiting = false;
            bool changed = false;
            for (int i = 0; i < _built.Count; i++)
            {
                BuiltSlot slot = _built[i];
                if (slot.Button != null || slot.ButtonAttempts >= MaxButtonAttempts) continue;
                if (Time.frameCount >= slot.NextButtonAttemptFrame)
                {
                    slot.ButtonAttempts++;
                    if (CreateButton(map, panel, slot)) changed = true;
                    else slot.NextButtonAttemptFrame = Time.frameCount + ButtonRetryFrames;
                }
                if (slot.Button == null && slot.ButtonAttempts < MaxButtonAttempts) waiting = true;
            }

            if (changed)
            {
                _pendingButtonLayout = true;
                _layoutAttempts = 0;
                _nextLayoutAttemptFrame = 0;
            }
            if (_pendingButtonLayout && !panel.InControlState && Time.frameCount >= _nextLayoutAttemptFrame)
            {
                _layoutAttempts++;
                try
                {
                    List<Button> panelButtons = PanelButtonsRef(panel);
                    if (panelButtons != null) panelButtons.RemoveAll(button => button == null);
                    panel.SetMaximizeEnabled(true);
                    panel.ResizeToFit(panel.ButtonListParent.childCount);
                    // Maximize returns immediately if already open. Defer this refresh while a
                    // call is selected, then re-open to apply the new width and scroll arrows.
                    panel.Minimize(true);
                    panel.Maximize(true);
                    LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)panel.transform);
                    UpdateScrollButtons(panel);
                    _pendingButtonLayout = false;
                }
                catch (Exception)
                {
                    _pendingButtonLayout = _layoutAttempts < MaxButtonAttempts;
                    _nextLayoutAttemptFrame = Time.frameCount + ButtonRetryFrames;
                }
            }
            waiting |= _pendingButtonLayout;
            _buttonsCreated = !waiting;
            _pendingButtons = waiting;
        }

        private static bool CreateButton(MapController map, MapFireSupportPanel panel, BuiltSlot slot)
        {
            GameObject instance = null;
            Button button = null;
            List<Button> panelButtons = null;
            try
            {
                GameObject prefab = ButtonPrefabRef(panel);
                if (prefab == null)
                {
                    throw new InvalidOperationException("button prefab reference is missing");
                }

                // Instantiate the button INACTIVE so its MapIconControlType.Awake does not run before we
                // have set the flag: the game's Awake indexes STATE_LABELS by MapControlType, and the
                // button prefab's own value is None - which is not in that table, so Awake threw
                // "KeyNotFoundException: The given key 'None' was not present in the dictionary" once per
                // slot, on every map open, and aborted its own initialisation (no click listener, no
                // status text). Deactivating the source prefab for the instantiation is the way to defer
                // Awake; it is restored immediately afterwards.
                bool prefabWasActive = prefab.activeSelf;
                if (prefabWasActive)
                {
                    prefab.SetActive(false);
                }
                try
                {
                    instance = UnityEngine.Object.Instantiate(prefab, panel.ButtonListParent);
                }
                finally
                {
                    if (prefabWasActive)
                    {
                        prefab.SetActive(true);
                    }
                }

                instance.name = "CustomFireSupportButton_Slot" + slot.Config.Index + "_" + ButtonNameSuffix(slot.DisplayName);
                instance.transform.SetAsLastSibling();
                MapIconControlType control = instance.GetComponent<MapIconControlType>();
                if (control == null)
                {
                    throw new InvalidOperationException("button prefab has no MapIconControlType");
                }

                MapControlFlag flag = FireSupportTemplates.ToMapControlFlag(slot.Config.Kind);
                control.MapControlType = flag;
                control.SupportName = string.IsNullOrEmpty(slot.DisplayName) ? slot.Config.DisplayName : slot.DisplayName;
                control.AddMapSupportInfo(slot.Info);

                // The prefab keeps whatever status/time text its designer left in it until Awake runs,
                // and Awake does not run at all while the panel is inactive - so normalize it here.
                try
                {
                    control.SetCooldownReady();
                }
                catch
                {
                }
                try
                {
                    Sprite icon = LookupIcon(panel, flag);
                    if (icon != null)
                    {
                        control.Icon = icon;
                    }
                }
                catch
                {
                    // The icon is cosmetic; never let it abort the button.
                }

                button = instance.GetComponent<Button>();
                if (button == null) throw new InvalidOperationException("button prefab has no Button");
                panelButtons = PanelButtonsRef(panel);
                if (panelButtons == null) throw new InvalidOperationException("panel button list is missing");
                int listIndex = panelButtons.Count;
                for (int i = _built.IndexOf(slot) + 1; i < _built.Count; i++)
                {
                    GameObject next = _built[i].Button;
                    if (next == null) continue;
                    // A recovered slot belongs before later slots, even if they succeeded first.
                    instance.transform.SetSiblingIndex(next.transform.GetSiblingIndex());
                    int nextIndex = panelButtons.IndexOf(next.GetComponent<Button>());
                    if (nextIndex >= 0) listIndex = nextIndex;
                    break;
                }
                panelButtons.Insert(listIndex, button);
                button.interactable = MissionStateController.CurrentState != MissionState.Planning;

                map.AvailableControlFlags = map.AvailableControlFlags | flag;

                // Everything the button needs is set - now let Awake run (it registers the click listener
                // and writes the first status label, both with the flag we just gave it).
                if (!instance.activeSelf)
                {
                    instance.SetActive(true);
                }
                slot.Button = instance;
                _buttons.Add(instance);
                return true;
            }
            catch (Exception)
            {
                if (panelButtons != null && button != null) panelButtons.Remove(button);
                if (instance != null)
                {
                    instance.SetActive(false);
                    instance.transform.SetParent(null, false);
                    UnityEngine.Object.Destroy(instance);
                }
                return false;
            }
        }

        /// <summary>Object-name friendly suffix for a button (letters/digits kept, everything else '_').</summary>
        private static string ButtonNameSuffix(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
            {
                return "support";
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder(displayName.Length);
            for (int i = 0; i < displayName.Length; i++)
            {
                char c = displayName[i];
                builder.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            return builder.ToString();
        }

        private static Sprite LookupIcon(MapFireSupportPanel panel, MapControlFlag flag)
        {
            try
            {
                // _supportIconMapping is a private nested RotaryHeart SerializableDictionaryBase, which
                // implements IDictionary<TKey,TValue> but NOT the non-generic IDictionary - casting to the
                // generic interface is the only way to read it (an "as IDictionary" cast always failed).
                IDictionary<MapControlFlag, Sprite> mapping =
                    SupportIconMappingRef(panel) as IDictionary<MapControlFlag, Sprite>;
                if (mapping == null)
                {
                    return null;
                }

                Sprite sprite;
                return mapping.TryGetValue(flag, out sprite) ? sprite : null;
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Teardown / rebuild
        // ------------------------------------------------------------------

        internal static void ResetForScene()
        {
            FireSupportPatches.CasTargetSpreadPatch.ResetForScene();
            // Called only when the game replaces the base scene, not for additive scene loads.
            _preparedThisMission = false;
            Teardown(true);
        }

        /// <summary>Removes everything this mod added, optionally destroying the buttons it created.</summary>
        internal static void Teardown(bool destroyButtons)
        {
            DeinjectArtillery();
            DeinjectAirframes();

            if (destroyButtons)
            {
                ClearButtons();
            }

            _buttons.Clear();
            _artillery.Clear();
            _cas.Clear();
            _rerolledFrame.Clear();
            _built.Clear();
            _pendingButtons = false;
            _buttonWaitFrames = 0;
            _buttonsCreated = false;
            _hideVanillaThisMission = false;
            _fireManager = null;
            _casManager = null;
            _createdCasManager = false;
            _casDeployPlaced = false;
            _playerCasCall = false;
            _markNextSortie = false;
            _buttonMap = null;
            _buttonPanel = null;
        }

        private static void DeinjectArtillery()
        {
            if (_fireManager == null || _artillery.Count == 0)
            {
                return;
            }

            RemoveFromArray(ref _fireManager.BlueArtilleryBatteries);
            RemoveFromArray(ref _fireManager.RedArtilleryBatteries);
        }

        private static void RemoveFromArray(ref ArtilleryBattery[] array)
        {
            if (array == null || array.Length == 0)
            {
                return;
            }

            List<ArtilleryBattery> kept = new List<ArtilleryBattery>(array.Length);
            for (int i = 0; i < array.Length; i++)
            {
                if (!IsOurBattery(array[i]))
                {
                    kept.Add(array[i]);
                }
            }
            array = kept.ToArray();
        }

        private static void DeinjectAirframes()
        {
            if (_casManager == null || _cas.Count == 0)
            {
                return;
            }

            RemoveFromArray(ref _casManager.BlueCasAirframes);
            RemoveFromArray(ref _casManager.RedCasAirframes);
        }

        private static void RemoveFromArray(ref CasAirframeUnit[] array)
        {
            if (array == null || array.Length == 0)
            {
                return;
            }

            List<CasAirframeUnit> kept = new List<CasAirframeUnit>(array.Length);
            for (int i = 0; i < array.Length; i++)
            {
                if (!IsOurAirframe(array[i]))
                {
                    kept.Add(array[i]);
                }
            }
            array = kept.ToArray();
        }

        // ------------------------------------------------------------------
        // Queries used by the Harmony patches
        // ------------------------------------------------------------------

        /// <summary>
        /// True while MapController.TryCallCAS (the player's map click) is executing. Scripted CAS
        /// events call CasSupportManager.SendCasSupport directly, and those must never be hijacked
        /// into the player's selected custom sortie.
        /// </summary>
        internal static bool PlayerCasCallInProgress
        {
            get { return _playerCasCall; }
        }

        internal static void BeginPlayerCasCall()
        {
            _playerCasCall = true;
        }

        internal static void EndPlayerCasCall()
        {
            _playerCasCall = false;
        }

        /// <summary>
        /// Set while a CAS call we routed to one of our own airframes is being spawned, and consumed
        /// by the CASController.SetLoadout prefix. That is how the summoned aircraft gets its
        /// CustomCasMarker even when it flies the game's own loadout (a rockets / bombs slot whose
        /// airframe already carries the right pylons), so the fire-time patches can recognise it.
        /// </summary>
        internal static void MarkNextSpawnedSortie()
        {
            _markNextSortie = true;
        }

        internal static void ClearMarkedSortie()
        {
            _markNextSortie = false;
        }

        internal static bool ConsumeMarkedSortie()
        {
            bool marked = _markNextSortie;
            _markNextSortie = false;
            return marked;
        }

        internal static bool IsOurSupportInfo(object supportInfo)
        {
            ArtilleryBattery battery = supportInfo as ArtilleryBattery;
            if (battery != null)
            {
                return IsOurBattery(battery);
            }

            CasAirframeUnit airframe = supportInfo as CasAirframeUnit;
            if (airframe != null)
            {
                return IsOurAirframe(airframe);
            }
            return false;
        }

        private static bool IsOurBattery(ArtilleryBattery battery)
        {
            if (battery == null)
            {
                return false;
            }
            for (int i = 0; i < _artillery.Count; i++)
            {
                if (ReferenceEquals(_artillery[i].Battery, battery))
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool IsOurAirframe(CasAirframeUnit airframe)
        {
            if (airframe == null)
            {
                return false;
            }
            for (int i = 0; i < _cas.Count; i++)
            {
                if (ReferenceEquals(_cas[i].Airframe, airframe))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Re-rolls the airframe AND its loadout for the slot being called, so a bomb or rocket slot
        /// sends a different aircraft with a different payload every time. Gun-run slots are left alone
        /// on purpose: their airframe is the designated one (Blue = A-10, Red = Su-22 or Su-25).
        ///
        /// The re-roll MUTATES the slot's existing CasAirframeUnit - it never replaces it. That object
        /// is shared by three things that all match it by reference:
        ///
        ///   * the manager's airframe array, which the spawned CASController reads back through its
        ///     frameNum and which CustomSupportRegistry maps to the slot;
        ///   * the map panel button, which keeps it in MapIconControlType.SupportInfos;
        ///   * MapController.TryCallCAS, which matches the buttons of a call with
        ///     "SupportInfos.Contains(result.SupportInfo)" and then tracks their status display against
        ///     them.
        ///
        /// Swapping the object in the array (as this used to) left the button pointing at an airframe
        /// nobody updated any more: the panel's button kept its "Firing..." label forever (no cooldown
        /// was ever tracked for it), and the next click on that button could not resolve the airframe at
        /// all, so SendCasSupport silently fell back to vanilla's "first ready airframe" - which is how a
        /// rocket slot ended up summoning the gun-run A-10.
        ///
        /// Only the three fields that describe WHICH aircraft is sent are touched; the mission count and
        /// cooldown stay on the object, so a re-roll cannot refill the slot.
        /// </summary>
        internal static bool TryRerollAirframeForCall(Faction faction, CasAirframeUnit airframe)
        {
            try
            {
                CasSlot slot;
                if (airframe == null || !TryGetCasSlot(airframe, out slot) || slot.Config == null ||
                    slot.Templates == null)
                {
                    return false;
                }
                if (slot.Config.AttackTypes != null &&
                    Array.IndexOf(slot.Config.AttackTypes, AttackKind.GunRun) >= 0)
                {
                    return false; // gun run: designated airframe, must not change.
                }

                // ONE DRAW PER CLICK. A single map click dispatches MapController.TryCallCAS once per
                // surviving subscriber (the game never unsubscribes its CAS handler - see
                // CasCallReadinessRepair), so this method runs two or more times for one click, and every
                // run rebuilds the slot. Without this guard the second dispatch re-rolls the aircraft out
                // from under the clone the first dispatch is already spawning: the airframe unit's prefab
                // and loadout change mid-call, and the sortie that reaches SetLoadout is not the one that
                // was drawn. The first draw in a frame wins; a later, genuinely new click still re-rolls.
                int frame = Time.frameCount;
                int rerolledAt;
                if (_rerolledFrame.TryGetValue(slot.Config.Index, out rerolledAt) && rerolledAt == frame)
                {
                    return false;
                }
                _rerolledFrame[slot.Config.Index] = frame;

                // Re-roll the airframe for this call: a bomb / rocket slot flies a different aircraft
                // each time. Safe now that every candidate comes from the bundle's name-keyed catalogue
                // (CasPrewarmer.BundleAirframeNames), so a draw always yields a summonable prefab asset -
                // the old scene-scan roster is what made a redraw land on an object whose
                // CASController.Start() never ran.
                CasSlot fresh = CustomSlotBuilder.BuildCas(slot.Config, slot.Templates, faction);
                if (fresh == null || fresh.Airframe == null || fresh.Airframe.airframePrefab == null)
                {
                    return false;
                }
                if (ReferenceEquals(fresh.Airframe.airframePrefab, airframe.airframePrefab) &&
                    ReferenceEquals(fresh.Airframe.Loadout, airframe.Loadout))
                {
                    return false; // exactly the same aircraft + loadout drawn again: nothing to re-roll
                }

                airframe.airframePrefab = fresh.Airframe.airframePrefab;
                airframe.Loadout = fresh.Airframe.Loadout;
                airframe.flyoverType = fresh.Airframe.flyoverType;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Weapon type configured for one of our batteries (used by the WeaponType getter patch).</summary>
        internal static bool TryGetWeaponType(ArtilleryBattery battery, out IndirectFireWeaponType weaponType)
        {
            weaponType = IndirectFireWeaponType.Mortars;
            if (battery == null)
            {
                return false;
            }
            for (int i = 0; i < _artillery.Count; i++)
            {
                if (ReferenceEquals(_artillery[i].Battery, battery))
                {
                    weaponType = _artillery[i].WeaponType;
                    return true;
                }
            }
            return false;
        }

        /// <summary>The built artillery slot that owns this battery (used by the artillery patches).</summary>
        internal static bool TryGetArtillerySlot(ArtilleryBattery battery, out ArtillerySlot slot)
        {
            slot = null;
            if (battery == null)
            {
                return false;
            }
            for (int i = 0; i < _artillery.Count; i++)
            {
                if (ReferenceEquals(_artillery[i].Battery, battery))
                {
                    slot = _artillery[i];
                    return true;
                }
            }
            return false;
        }

        /// <summary>The built CAS slot that owns this airframe (used by the CAS patches).</summary>
        internal static bool TryGetCasSlot(CasAirframeUnit airframe, out CasSlot slot)
        {
            slot = null;
            if (airframe == null)
            {
                return false;
            }
            for (int i = 0; i < _cas.Count; i++)
            {
                if (ReferenceEquals(_cas[i].Airframe, airframe))
                {
                    slot = _cas[i];
                    return true;
                }
            }
            return false;
        }

        internal static bool HasCustomArtilleryFor(Faction faction)
        {
            return _artillery.Count > 0 && faction == _playerFaction;
        }

        internal static bool HasReadyCustomArtillery(Faction faction, IndirectFireMunitionType munition)
        {
            if (_artillery.Count == 0 || faction != _playerFaction)
            {
                return false;
            }

            for (int i = 0; i < _artillery.Count; i++)
            {
                ArtilleryBattery battery = _artillery[i].Battery;
                if (battery != null && battery.HasMunitionType(munition) && battery.IsReadyToFire)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The custom airframe the player currently has selected on the map panel, if any. Vanilla
        /// SendCasSupport has no "which button" parameter, so the prefix needs this to dispatch the
        /// right sortie.
        /// </summary>
        internal static bool TryGetActiveCasAirframe(Faction faction, out CasAirframeUnit airframe)
        {
            airframe = null;
            if (_cas.Count == 0)
            {
                return false;
            }

            MapController map = MapController.Instance;
            if (map == null || map.FireSupportPanel == null)
            {
                return false;
            }

            CasAirframeUnit active = map.FireSupportPanel.ActiveSupportInfo as CasAirframeUnit;
            if (active == null || !IsOurAirframe(active))
            {
                return false;
            }

            airframe = active;
            return true;
        }

        // ------------------------------------------------------------------
        // Environment helpers
        // ------------------------------------------------------------------

        private static bool IsNight()
        {
            CelestialSky sky = CelestialSky.Instance;
            return sky != null && sky.IsNight;
        }

        /// <summary>
        /// The faction resolved while the slots were built can predate the player unit (or simply
        /// disagree with it). Both FireMissionManager and CasSupportManager index the array of the
        /// faction a call is made with, so a slot injected on the wrong side is unreachable: the
        /// player would click the map and get a vanilla strike, or nothing at all. Re-check here,
        /// before anything is injected.
        /// </summary>
        private static void CorrectPlayerFaction()
        {
            Faction faction = CurrentPlayerFaction(_playerFaction);
            if (faction == _playerFaction)
            {
                return;
            }

            _playerFaction = faction;
        }

        /// <summary>Authoritative player faction right now; falls back to the given value.</summary>
        private static Faction CurrentPlayerFaction(Faction fallback)
        {
            try
            {
                PlayerInput input = PlayerInput.Instance;
                if (input != null && input.CurrentPlayerUnit != null && input.CurrentPlayerUnit.Allegiance != Faction.Neutral)
                {
                    return input.CurrentPlayerUnit.Allegiance;
                }
            }
            catch
            {
            }
            return fallback;
        }

        private static Faction ResolvePlayerFaction()
        {
            PlayerInput input = PlayerInput.Instance;
            if (input != null && input.CurrentPlayerUnit != null && input.CurrentPlayerUnit.Allegiance != Faction.Neutral)
            {
                return input.CurrentPlayerUnit.Allegiance;
            }
            if (SceneController.TargetSpawningFaction != Faction.Neutral)
            {
                return SceneController.TargetSpawningFaction;
            }
            if (input != null && input.CurrentPlayerUnit != null)
            {
                return input.CurrentPlayerUnit.Allegiance;
            }
            return Faction.Blue;
        }

        private static FireMissionManager EnsureFireMissionManager(MapController mapController)
        {
            FireMissionManager manager = UnityEngine.Object.FindObjectOfType<FireMissionManager>();
            if (manager == null)
            {
                GameObject host = new GameObject("CustomFireSupport_FireMissionManager");
                manager = host.AddComponent<FireMissionManager>();
            }

            if (mapController != null && MapFireManagerRef(mapController) == null)
            {
                MapFireManagerRef(mapController) = manager;
            }
            return manager;
        }

        private static CasSupportManager EnsureCasSupportManager(MapController mapController)
        {
            CasSupportManager manager = UnityEngine.Object.FindObjectOfType<CasSupportManager>();
            if (manager == null)
            {
                GameObject host = new GameObject("CustomFireSupport_CasSupportManager");
                manager = host.AddComponent<CasSupportManager>();

                // CasSupportManager.Start()/Update() walk BlueCasAirframes/RedCasAirframes without a
                // null check, and the map's InitControlState calls HasAirframesAvailable() (which reads
                // PlayerFactionAirframes.Length) before the custom airframes are injected. Seed empty
                // arrays so a mission that ends up with no CAS slot cannot throw every frame, and so
                // the injection below always appends into a real array.
                manager.BlueCasAirframes = new CasAirframeUnit[0];
                manager.RedCasAirframes = new CasAirframeUnit[0];

                _createdCasManager = true;
                _casDeployPlaced = PlaceDeployPoints(manager);
            }

            if (mapController != null && MapCasManagerRef(mapController) == null)
            {
                MapCasManagerRef(mapController) = manager;
            }
            return manager;
        }

        /// <summary>
        /// Deploy point for a manager the mod had to create. GHPC only uses it as the spawn / exfil
        /// location of the aircraft, so putting it behind the player keeps the fly-in sensible.
        /// Returns false (and leaves the point untouched) while the player unit does not exist yet -
        /// EnsureCreatedCasDeployPoints() retries once it does.
        /// </summary>
        private static bool PlaceDeployPoints(CasSupportManager manager)
        {
            if (manager == null)
            {
                return false;
            }

            try
            {
                PlayerInput input = PlayerInput.Instance;
                if (input == null || input.CurrentPlayerUnit == null)
                {
                    return false;
                }

                Vector3 origin = input.CurrentPlayerUnit.transform.position;
                float bearing = _global.CasDeployBearingDegrees * Mathf.Deg2Rad;
                Vector2 point = new Vector2(
                    origin.x + Mathf.Sin(bearing) * _global.CasDeployDistanceMeters,
                    origin.z + Mathf.Cos(bearing) * _global.CasDeployDistanceMeters);

                BlueDeployPointRef(manager) = point;
                RedDeployPointRef(manager) = point;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Places the deploy point of a manager the mod created once the player unit exists. Called
        /// every frame from Tick() and again from the SendCasSupport prefix, because in a mission
        /// without CAS the map initialises before the player unit in some scenarios.
        /// </summary>
        internal static void EnsureCreatedCasDeployPoints()
        {
            if (!_createdCasManager || _casDeployPlaced || _casManager == null)
            {
                return;
            }
            _casDeployPlaced = PlaceDeployPoints(_casManager);
        }

        internal static void EnsureCooldownManager()
        {
            // Safety net for missions without a CooldownManager: vanilla TryCallFireMission /
            // TryCallCAS dereference CooldownManager.Instance without a null check.
            if (CooldownManager.Instance != null)
            {
                return;
            }
            GameObject host = new GameObject("CustomFireSupport_CooldownManager");
            host.AddComponent<CooldownManager>();
        }
    }
}


