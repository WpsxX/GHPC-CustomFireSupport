using System.Text;

namespace CustomFireSupport
{
    /// <summary>
    /// Generates the [CustomFireSupport] section that CfgFile writes into MelonPreferences.cfg the
    /// first time the mod runs. Keeping it in code means the file always documents exactly the keys the
    /// reader understands (tools/check_docs.py also cross-checks it against docs/SampleConfig.cfg).
    ///
    /// Every entry carries its comment in BOTH languages in a single call, so the Chinese section the mod
    /// writes and the English twin shipped as docs/SampleConfig.en.cfg can never drift apart in keys or
    /// values - only the comment text differs.
    /// </summary>
    internal static class DefaultConfigSection
    {
        /// <summary>Comment language of a generated section.</summary>
        internal enum Lang
        {
            /// <summary>The shipped player-facing text: what the mod writes at first run.</summary>
            Chinese,

            /// <summary>The English twin documented as docs/SampleConfig.en.cfg.</summary>
            English,
        }

        /// <summary>The section the mod writes on first run (Chinese, the shipped player-facing text).</summary>
        internal static string Build()
        {
            return Build(Lang.Chinese);
        }

        internal static string Build(Lang lang)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[CustomFireSupport]");
            sb.AppendLine(Pick(lang,
                "# CustomFireSupport 配置（本段由 mod 自己解析，不经 MelonLoader 的 entry 层）。",
                "# CustomFireSupport config (parsed by the mod itself, not by MelonLoader's entry layer)."));
            sb.AppendLine(Pick(lang,
                "# 改完重开任务生效（槽位只在任务开始时构建）；HideVanillaFireSupport 同样需重开任务。",
                "# Changes apply after re-starting the mission (slots are built at mission start); HideVanillaFireSupport too."));
            sb.AppendLine(Pick(lang,
                "# 槽位键带 SlotN_ 前缀；6 个槽位默认全部开启，用不到的槽把它的 Enabled 改成 false 即可。",
                "# Slot keys carry a SlotN_ prefix; all six slots are on by default - set Enabled = false to drop one."));
            sb.AppendLine(Pick(lang,
                "# 默认布局：1 = 常规炮击，2 = 烟幕，3 = 照明弹，4/5/6 = CAS（机炮扫射 / 火箭弹 / 炸弹）。",
                "# Default layout: 1 = artillery, 2 = smoke, 3 = illumination, 4/5/6 = CAS (gun run / rockets / bombs)."));
            sb.AppendLine();

            // ---- global -------------------------------------------------
            Entry(sb, "Enabled", "true", Pick(lang,
                "总开关：false = mod 完全不动作",
                "master switch: false = the mod does nothing at all"));
            Entry(sb, "HideVanillaFireSupport", "true", Pick(lang,
                "true = 地图上只显示你的槽位（官方炮组/架次数组不动，脚本火力照常）",
                "true = only your slots show on the map (the vanilla battery / sortie arrays are untouched, scripted fire support still runs)"));
            Entry(sb, "IlluminationOnlyAtNight", "true", Pick(lang,
                "true = 照明槽位只在夜间出现（默认）；false = 白天也能用",
                "true = illumination slots appear at night only (default); false = usable by day too"));
            Entry(sb, "SmokeOnlyDuringDay", "true", Pick(lang,
                "true = 烟幕槽位只在白天出现（默认）；夜间烟雾看不见，自动隐藏；false = 全天可用",
                "true = smoke slots appear during the day only (default); smoke is invisible at night, so they auto-hide; false = usable around the clock"));
            Entry(sb, "VerboseLogging", "false", Pick(lang,
                "true = 把模板发现/槽位解析细节打印到日志",
                "true = print template discovery / slot parsing details to the log"));
            Entry(sb, "CasDeployDistanceMeters", "8000.0", Pick(lang,
                "仅当关卡没有 CasSupportManager 时：自建管理器的飞机起飞点距玩家起始位置多远（米）",
                "only when the mission has no CasSupportManager: how far the self-built manager's aircraft spawn point sits from the player's start position (metres)"));
            Entry(sb, "CasDeployBearingDegrees", "180.0", Pick(lang,
                "上述起飞点方位角（度），0 = +Z（北），顺时针",
                "bearing of that spawn point (degrees), 0 = +Z (north), increasing clockwise"));
            Entry(sb, "CasPrewarmKeys", "\"auto\"", Pick(lang,
                "会话启动时额外预加载的 Addressables 键（逗号分隔）；auto = 无（CAS 机型/挂架/弹药都由随 mod 的 cas_assets 包提供，无需额外键）。空 = 关",
                "extra Addressables keys to preload at session start (comma-separated); auto = none (CAS airframes / hardpoints / ammo all ship in the cas_assets pack, so no extra key is needed); empty = off"));
            sb.AppendLine();

            // ---- slots --------------------------------------------------
            for (int n = 1; n <= 6; n++)
            {
                AppendSlot(sb, n, lang);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static void AppendSlot(StringBuilder sb, int n, Lang lang)
        {
            string p = "Slot" + n + "_";
            bool cas = n >= 4;

            sb.AppendLine(Pick(lang, "# ===== 槽位 " + n + " =====", "# ===== slot " + n + " ====="));
            Entry(sb, p + "Enabled", "true", Pick(lang,
                "槽位开关（默认全开）",
                "slot switch (all six are on by default)"));
            Entry(sb, p + "Type", "\"" + TypeFor(n) + "\"", Pick(lang,
                "Artillery | ArtillerySmoke | ArtilleryIllumination | CASSupport（固定翼 CAS）；别名 HE/Smoke/Illum/CAS 也认",
                "Artillery | ArtillerySmoke | ArtilleryIllumination | CASSupport (fixed-wing CAS); the aliases HE/Smoke/Illum/CAS are accepted too"));
            Entry(sb, p + "DisplayName", "\"" + NameFor(n) + "\"", Pick(lang,
                "地图按钮上的名字",
                "name shown on the map button"));
            Entry(sb, p + "Missions", cas ? "2" : "3", Pick(lang,
                "可用呼叫次数（CAS 为架次）：-1 = 无限",
                "number of calls (sorties for CAS): -1 = unlimited"));
            Entry(sb, p + "RoundsPerCall", "-1", Pick(lang,
                "单次弹数：-1 = 用游戏自己炮组的弹数；>0 = 指定",
                "rounds per call: -1 = use the game battery's own count; >0 = force it"));
            Entry(sb, p + "Munition", "\"" + DefaultMunition(n) + "\"", Pick(lang,
                "弹种（仅火炮）：AntiPersonnel | AntiArmor | Smoke | Illumination；留空 = 随槽位类型",
                "shell type (artillery only): AntiPersonnel | AntiArmor | Smoke | Illumination; empty = follow the slot type"));
            Entry(sb, p + "Weapon", "\"Any\"", Pick(lang,
                "Guns | Mortars | Rockets | Any（仅影响 WeaponType 属性）",
                "Guns | Mortars | Rockets | Any (only affects the WeaponType property)"));
            Entry(sb, p + "ImpactDelaySeconds", "1.0", Pick(lang,
                "首弹延迟倍率：1 = 游戏原值；<1 = 取消首弹延迟（首弹立即到）；0/负数 = 整梭子当帧打完。只影响首弹，不压缩发间隔",
                "time-to-first-impact scale: 1 = the game's own value; <1 = drop the delay (first round lands instantly); 0/negative = the whole volley in a single frame. Only the first round - the interval is untouched"));
            Entry(sb, p + "InterShotDelaySeconds", "1.0", Pick(lang,
                "发间隔倍率（发间隔只由本键决定）：1 = 原值（通常 0.7 秒），0.5 = 一半，0 = 同一帧打完",
                "interval-between-rounds scale (this key alone decides the interval): 1 = the game's own value (about 0.7 s), 0.5 = half, 0 = every round in the same frame"));
            Entry(sb, p + "DispersionMeters", "1.0", Pick(lang,
                "散布倍率：1 = 原值，0.5 = 一半，0 = 全部同一点（仅火炮）",
                "dispersion scale: 1 = the game's own radius, 0.5 = half, 0 = every round on the same spot (artillery only)"));
            Entry(sb, p + "CooldownSeconds", "1.0", Pick(lang,
                "冷却：火炮 = 倍率（1=原值）；CAS = 直接秒数（已去除游戏原生 120 秒）；-1/0 = 无冷却",
                "cooldown: artillery = a scale (1 = the game's own value); CAS = seconds directly (the native 120 s is removed); -1/0 = no cooldown"));
            Entry(sb, p + "AmmoName", "\"\"", Pick(lang,
                "可选：弹药名包含该文本（如 155mm）；留空 = 取该弹种第一个模板",
                "optional: only rounds whose name contains this text (e.g. 155mm); empty = take the first template of the shell type"));
            Entry(sb, p + "CasFlyover", "\"\"", Pick(lang,
                "飞行方式：留空 = 按机型自动；也可写 SinglePass（单次通过）| Linger（盘旋约 60 秒）",
                "flyover profile: empty = automatic per airframe; or SinglePass (one pass) | Linger (circle for about 60 s)"));
            Entry(sb, p + "CasAccuracy", "1.0", Pick(lang,
                "CAS 落点圆半径 = 值 × 15 米（0–1 之间按百分比；0.5 = 7.5 m，1 = 15 m，>1 更大）：0 = 每一发都飞进目标中心（机炮/火箭弹/炸弹都精确命中）；**对空地导弹无效**——它固定 1 发、永远 100% 命中锁定目标中心；对游戏自带导弹挂载也无效（仍由游戏自己制导）",
                "CAS impact circle radius = value x 15 m (between 0 and 1 read as a percentage; 0.5 = 7.5 m, 1 = 15 m, >1 larger): 0 = every round flies into the target's centre (guns / rockets / bombs all hit precisely); **does not apply to air-to-ground missiles** - one per sortie, always a 100% hit on the locked target's centre; it does not apply to the game's own missile loadouts either (they stay under the game's guidance)"));
            Entry(sb, p + "CasAttackTypes", "\"" + AttacksFor(n) + "\"", Pick(lang,
                "攻击方式：Any | 逗号列表 Bombs,Rockets,AirToGroundMissile(AGM/ATGM/Missile),GunRun（只保留机型实际挂载的；空地导弹机型固定为 A-10/MiG-23BN）；留空 = 用机型自带挂载。空空导弹与训练弹（AirToAirMissile / Inert）已移除，写了会被忽略并记警告",
                "attack types: Any | comma list Bombs,Rockets,AirToGroundMissile(AGM/ATGM/Missile),GunRun (only what the airframe actually carries is kept; the AGM airframes are fixed to A-10 / MiG-23BN); empty = use the airframe's own loadout. Air-to-air missiles and training rounds (AirToAirMissile / Inert) have been removed - writing them is ignored with a warning"));
            sb.AppendLine(Pick(lang,
                "# 机型 / 弹药 / 生成高度 / 入射角 / 来向 / 再装填全部交给游戏自己处理，不用配",
                "# airframe / ammo / spawn altitude / impact angle / approach direction / reload are all left to the game - nothing to configure"));
        }

        /// <summary>Default CasAttackTypes per slot: the three CAS slots ship as gun run / rockets / bombs.</summary>
        internal static string AttacksFor(int n)
        {
            switch (n)
            {
                case 4: return "GunRun";
                case 5: return "Rockets";
                case 6: return "Bombs";
                default: return "Any";
            }
        }

        /// <summary>The comment of one entry in the requested language.</summary>
        private static string Pick(Lang lang, string chinese, string english)
        {
            return lang == Lang.English ? english : chinese;
        }

        private static void Entry(StringBuilder sb, string key, string value, string comment)
        {
            sb.Append(key).Append(" = ").Append(value).Append("  # ").Append(comment).AppendLine();
        }

        /// <summary>
        /// Slot type of the shipped layout: 1 artillery, 2 smoke, 3 illumination, 4/5/6 CAS.
        ///
        /// This is the ONE definition of the layout. The generated section and the reader's fallback for
        /// a missing key both call it, so a config that omits SlotN_Type behaves exactly like the default
        /// the file it was generated from documents - the two used to be separate switch statements.
        /// </summary>
        internal static string TypeFor(int n)
        {
            switch (n)
            {
                case 2: return "ArtillerySmoke";
                case 3: return "ArtilleryIllumination";
                case 4:
                case 5:
                case 6: return "CASSupport";
                default: return "Artillery";
            }
        }

        /// <summary>Button name of the shipped layout, so the three CAS slots are told apart.</summary>
        internal static string NameFor(int n)
        {
            switch (n)
            {
                case 2: return "Custom Smoke";
                case 3: return "Custom Illumination";
                case 4: return "CAS Gun Run";
                case 5: return "CAS Rockets";
                case 6: return "CAS Bombs";
                default: return "Custom Artillery";
            }
        }

        /// <summary>Default Munition per slot; the CAS slots ignore it.</summary>
        private static string DefaultMunition(int n)
        {
            switch (n)
            {
                case 2: return "Smoke";
                case 3: return "Illumination";
                default: return "AntiPersonnel";
            }
        }
    }
}
