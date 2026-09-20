# CustomFireSupport — config file guide

<div align="center">

![English](https://img.shields.io/badge/English-0969da?style=flat-square)

![简体中文](https://img.shields.io/badge/简体中文-2ea44f?style=flat-square)

**English** · [简体中文](README.zh-CN.md)

</div>

> Game: **Gunner, HEAT, PC!** (MelonLoader mod)>   
> Requires: MelonLoader 0.6.x + Harmony (bundled with MelonLoader)>   
> Version: **v1.0.1**

The mod puts **6 configurable slots** on the mission map's fire-support panel.  
Everything it does is decided by the `[CustomFireSupport]` section in  
`Bin\UserData\MelonPreferences.cfg` — **no code changes, no recompiling**.

This document covers only **what you can set yourself**: what every key means and what values it takes.

Built on the fire-support idea of [GHPC Artillery Rework](https://github.com/QwertyRyo/GHPC_Artillery_Rework)  
by QwertyRyo — see [License & Credits](#6-license--credits).

## Default layout (all six slots on by default)

| Slot | Default role           | Default payload / shell |
| ---- | ---------------------- | ----------------------- |
| 1    | Conventional artillery | HE anti-personnel       |
| 2    | Smoke                  | Smoke shell             |
| 3    | Illumination           | Illumination shell      |
| 4    | CAS air support        | Gun run                 |
| 5    | CAS air support        | Rockets                 |
| 6    | CAS air support        | Bombs                   |

Set a slot's `Enabled` to `false` if you do not need it; to repurpose a slot, see the value lists in §4.

---

## 0. Install and when it takes effect

1. Download the latest `CustomFireSupport-v1.0.1.zip` from this repository's **Releases** page.
2. Copy `CustomFireSupport.dll` from the zip into `<game>\Bin\Mods\`.
3. Copy `cas_assets` from the zip into the same `<game>\Bin\Mods\` folder (**it must sit next to the     
   DLL**). It is the asset pack shipped with the mod: CAS airframes, loadouts, hardpoints and the smoke /     
   illumination projectiles. With it installed, aircraft plus smoke / illumination are available in every     
   mission.

   `Bin\Mods\` then looks like this:
   ```
   Bin\Mods\CustomFireSupport.dll
   Bin\Mods\cas_assets
   ```
4. Start the game once (reaching the main menu is enough); the mod writes the `[CustomFireSupport]`     
   section into `MelonPreferences.cfg`. The zip also carries `CustomFireSupport.cfg` (and its     
   English-commented twin `CustomFireSupport.en.cfg`) with exactly the same content, either of which you     
   can use to overwrite that section instead.

   Log tag: `[CustomFireSupport]`. Log file: `Bin\MelonLoader\Latest.log`.

---

## 1. Writing rules

| Item            | Notes                                                                                                |
| --------------- | ---------------------------------------------------------------------------------------------------- |
| Section         | There is only one section, `[CustomFireSupport]`; every key lives in it                              |
| Key prefix      | Slot keys always carry a `SlotN_` prefix, `N` = 1..6, e.g. `Slot3_Missions`                          |
| Booleans        | `true` / `false`, also `1` / `0` / `yes` / `no` / `on` / `off`                                       |
| Strings         | Quotes are optional: `Type = Artillery` equals `Type = "Artillery"`                                  |
| Comments        | Whole-line comments, and trailing `# comment` (a `#` inside quotes is not a comment)                 |
| Case            | Key names and values are both case-insensitive                                                       |
| Scale semantics | `1.0` = the game's own value, `0.5` = half, `0` / `-1` = off (instant / no cooldown / no dispersion) |
| Duplicate keys  | The last one wins                                                                                    |
| Omitted keys    | A missing key uses its default — you never need all 98                                               |

**Total keys**: 8 global + 15 per slot × 6 slots = **98**.

---

## 2. Global settings (`[CustomFireSupport]`, 8 keys)

| Key                       | Type   | Default  | Values                                              | Meaning                                                                                                                                                                                                             |
| ------------------------- | ------ | -------- | --------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Enabled`                 | bool   | `true`   | `true` / `false`                                    | Master switch. `false` = the mod does nothing at all.                                                                                                                                                               |
| `HideVanillaFireSupport`  | bool   | `true`   | `true` / `false`                                    | `true` = the map panel shows only your slots. The game's own battery / sortie data is not modified and mission-scripted fire support still runs — only the buttons are hidden.                                      |
| `IlluminationOnlyAtNight` | bool   | `true`   | `true` / `false`                                    | `true` = illumination slots appear at night only (auto-hidden by day); `false` = usable by day too.                                                                                                                 |
| `SmokeOnlyDuringDay`      | bool   | `true`   | `true` / `false`                                    | `true` = smoke slots appear during the day only (auto-hidden at night); `false` = usable around the clock.                                                                                                          |
| `VerboseLogging`          | bool   | `false`  | `true` / `false`                                    | `true` = print template discovery and slot parsing details to the log.                                                                                                                                              |
| `CasDeployDistanceMeters` | float  | `8000.0` | `100` – `60000`                                     | Only for **missions that have no CAS manager of their own**: how far the aircraft's spawn / exit point sits from the player's start position (metres).                                                              |
| `CasDeployBearingDegrees` | float  | `180.0`  | `0` – `360` (wraps at 360)                          | Bearing of that spawn point (degrees). `0` = +Z (north), increasing clockwise.                                                                                                                                      |
| `CasPrewarmKeys`          | string | `"auto"` | `auto` / comma- or semicolon-separated keys / empty | Extra addressable keys to preload at session start. `auto` = load nothing extra (CAS airframes / loadouts / hardpoints and the smoke + illumination shells all ship in `cas_assets`); empty = disable this feature. |

---

## 3. Slot settings (`SlotN_*`, 15 keys per slot)

> Replace `N` with 1..6. The "Default" column follows the default layout.

| Key                           | Type   | Default                                                                                                                            | Values                                     | Meaning                                                                                                                                                                                |
| ----------------------------- | ------ | ---------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `SlotN_Enabled`               | bool   | `true`                                                                                                                             | `true` / `false`                           | Slot switch. All six are on by default.                                                                                                                                                |
| `SlotN_Type`                  | string | slot 1 = `Artillery`; slot 2 = `ArtillerySmoke`; slot 3 = `ArtilleryIllumination`; slots 4/5/6 = `CASSupport`                      | see §4.1                                   | Slot type.                                                                                                                                                                             |
| `SlotN_DisplayName`           | string | per the default layout (`Custom Artillery` / `Custom Smoke` / `Custom Illumination` / `CAS Gun Run` / `CAS Rockets` / `CAS Bombs`) | any text; empty = default name             | Name shown on the map button.                                                                                                                                                          |
| `SlotN_Missions`              | int    | slots 1/2/3 = `3`; slots 4/5/6 = `2`                                                                                               | `-1`, or `0` – `999`                       | Number of **calls**. `-1` = unlimited. `0` = not clickable.                                                                                                                            |
| `SlotN_RoundsPerCall`         | int    | `-1`                                                                                                                               | `-1` (or anything below 1), or `1` – `999` | **Rounds per call**. `-1` = use the game battery's own count; `>0` = force it.                                                                                                         |
| `SlotN_Munition`              | string | slot 2 = `Smoke`; slot 3 = `Illumination`; others = `AntiPersonnel`                                                                | see §4.2; empty = follow `Type`            | **Shell type**. A mismatch with `Type` still works.                                                                                                                                    |
| `SlotN_Weapon`                | string | `Any`                                                                                                                              | see §4.3                                   | Affects the weapon category (`WeaponType`) shown on the panel.                                                                                                                         |
| `SlotN_ImpactDelaySeconds`    | float  | `1.0`                                                                                                                              | `-1` – `100`                               | **Time-to-first-impact scale** `1` = the game's own value; `0`–`1` = drop the delay, first round lands instantly; `0` / negative = **in one frame**. **Does not affect the interval.** |
| `SlotN_InterShotDelaySeconds` | float  | `1.0`                                                                                                                              | `-1` – `100`                               | **Interval-between-rounds scale.** `1` = the game's own value; `0.5` = half; `0` = in one frame. **Does not affect the first-round delay.**                                            |
| `SlotN_DispersionMeters`      | float  | `1.0`                                                                                                                              | `-1` – `100`                               | **Dispersion scale.** `1` = the game's own radius; `0.5` = half; `0` / `-1` = every round on the same spot.                                                                            |
| `SlotN_CooldownSeconds`       | float  | `1.0`                                                                                                                              | `-1` – `100`                               | **Cooldown.** Artillery = a scale (`1` = the game's own value); CAS = seconds directly (the game's own 120 s is not applied). `-1` / `0` = no cooldown; `0.5` = half.                  |
| `SlotN_AmmoName`              | string | `""`                                                                                                                               | any text; empty = no filter                | Optional filter: only rounds whose **name contains this text** are picked, e.g. `"155mm"`. Empty = take the best-ranked template of the shell type.                                    |
| `SlotN_CasFlyover`            | string | `""`                                                                                                                               | `""` / `SinglePass` / `Linger`             | CAS **flyover profile**. Empty = automatic per airframe; `SinglePass` = one pass; `Linger` = circle. CAS slots only.                                                                   |
| `SlotN_CasAccuracy`           | float  | `1.0`                                                                                                                              | `-1` – `100`                               | CAS **impact circle radius = value × 15 m**: `0` (or `-1`) = radius 0 = the locked target's centre; `0.2` = 3 m; `0.5` = 7.5 m; `1` = 15 m; `>1` = larger. CAS slots only.             |
| `SlotN_CasAttackTypes`        | string | slot 4 = `GunRun`; slot 5 = `Rockets`; slot 6 = `Bombs`; others = `Any`                                                            | `Any`, or a comma-separated list, see §4.4 | CAS **attack-type filter**. Only types the airframe actually carries are kept. CAS slots only.                                                                                         |

**Artillery-only keys**: `RoundsPerCall`, `Munition`, `Weapon`, `ImpactDelaySeconds`, `InterShotDelaySeconds`, `DispersionMeters`, `AmmoName`.  
**CAS-only keys**: `CasFlyover`, `CasAccuracy`, `CasAttackTypes`.  
**Both kinds**: `Enabled`, `Type`, `DisplayName`, `Missions`, `CooldownSeconds`.

> `ImpactDelaySeconds` and `InterShotDelaySeconds` are **two independent keys**: one only says when the>   
> first round lands, the other only says how far apart the rounds are. For "keep the first-round delay as>   
> it is but halve the interval", write `ImpactDelaySeconds = 1.0` + `InterShotDelaySeconds = 0.5` — they>   
> never affect each other.
>
> Airframe, ammunition, spawn altitude, impact angle, approach direction and reload time need **no>   
> configuration**: the game's own values are used.

---

## 4. Value lists

### 4.1 `Type` (slot type)

| Value                   | Panel button | Purpose                                   |
| ----------------------- | ------------ | ----------------------------------------- |
| `Artillery`             | Artillery    | Conventional artillery (HE / anti-armour) |
| `ArtillerySmoke`        | Smoke        | Smoke screening                           |
| `ArtilleryIllumination` | Illum        | Illumination shells                       |
| `CASSupport`            | CAS          | Fixed-wing air support                    |

### 4.2 `Munition` (shell type, artillery only)

| Value           | What you see in game                                                                                                                                                       |
| --------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `AntiPersonnel` | HE anti-personnel shell (default)                                                                                                                                          |
| `AntiArmor`     | **Cluster cargo round**: scatters dual-purpose HEAT/fragmentation submunitions — a hit on a vehicle penetrates armour, and it is just as effective against nearby infantry |
| `Smoke`         | Smoke shell                                                                                                                                                                |
| `Illumination`  | Illumination shell                                                                                                                                                         |

### 4.3 `Weapon` (weapon category, artillery only)

`Guns` / `Mortars` / `Rockets` / `Any` (default `Any`).

### 4.4 `CasAttackTypes` (CAS attack types)

Values (comma-separated, multiple allowed; `Any` or empty = no filter, i.e. everything the airframe carries):

| Value                | Aliases                     | Effect                                                       |
| -------------------- | --------------------------- | ------------------------------------------------------------ |
| `Bombs`              | `Bomb`                      | Bombs                                                        |
| `Rockets`            | `Rocket`                    | Rockets                                                      |
| `GunRun`             | `Gun` / `Strafe` / `Cannon` | Gun strafing                                                 |
| `AirToGroundMissile` | `AGM` / `ATGM` / `Missile`  | Air-to-ground missile: A-10 / MiG-23BN, one round per sortie |

### 4.5 `CasFlyover` (CAS flyover profile)


`SinglePass` (one pass) / `Linger` (circle for about 60 s) / empty or `auto` (per airframe, everything else single pass).

### 4.6 `CasPrewarmKeys` (global)

`auto` (default, load nothing extra) / comma- or semicolon-separated addressable keys / empty (off).

---

## 5. Common recipes

| What you want                                  | What to write                                                                                   |
| ---------------------------------------------- | ----------------------------------------------------------------------------------------------- |
| Turn a slot off                                | Set its `Enabled` to `false` (e.g. no smoke: `Slot2_Enabled = false`)                           |
| Make slot 1 fire anti-armour (cluster)         | `Slot1_Munition = "AntiArmor"`                                                                  |
| Add another illumination slot                  | Take an unused slot, set its `Type` to `ArtilleryIllumination` and `Munition` to `Illumination` |
| Change what a CAS slot drops                   | `SlotN_CasAttackTypes = "GunRun"` / `"Rockets"` / `"Bombs"` / `"AirToGroundMissile"`            |
| One CAS slot, two weapon kinds                 | `SlotN_CasAttackTypes = "Bombs,Rockets"`                                                        |
| Unlimited calls                                | `SlotN_Missions = -1`                                                                           |
| No cooldown                                    | `SlotN_CooldownSeconds = -1`                                                                    |
| First round instant, rest at normal speed      | `SlotN_ImpactDelaySeconds = 0.5` (leave `InterShotDelaySeconds` at `1.0`)                       |
| Halve the interval, keep the first-round delay | `SlotN_InterShotDelaySeconds = 0.5` (leave `ImpactDelaySeconds` at `1.0`)                       |
| Instant volley (one frame)                     | `SlotN_ImpactDelaySeconds = 0` + `SlotN_InterShotDelaySeconds = 0`                              |
| Every round on the same spot                   | `SlotN_DispersionMeters = 0`                                                                    |
| CAS that always hits                           | `SlotN_CasAccuracy = 0`                                                                         |
| Artillery only, no aircraft                    | Set `Enabled = false` on slots 4/5/6                                                            |
| A clean map panel                              | `HideVanillaFireSupport = true` (default)                                                       |

---

## 6. License & Credits

Licensed under the **GNU Affero General Public License v3.0** (see [LICENSE](LICENSE)).

**[GHPC Artillery Rework](https://github.com/QwertyRyo/GHPC_Artillery_Rework)** by **QwertyRyo** — the  
community mod that first turned GHPC's fire support into something a player could reshape. The direction  
this mod took started there, and credit and thanks go to its author. No code is shared with it: the  
fire-support system here (the slots, the CAS payload factory, the cluster munition) is this mod's own  
implementation.
