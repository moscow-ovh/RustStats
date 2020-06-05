using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Network;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Rust;
using UnityEngine;
using UnityEngine.Networking;
using System.Collections;

namespace Oxide.Plugins
{
    [Info("Rust Stats", "Sanlerus", "1.0.1")]
    [Description("Rust stats")]
    public class RustStats : RustPlugin
    {
        #region Setting // v1.0

        protected override void LoadDefaultConfig() { } // Совместимость с .cs плагинами, создание пустого файла конфигурации.

        private class Setting
        {
            public string ConfigVersion;

            [JsonProperty(PropertyName = "Автоматический сброс статистики при вайпе карты")]
            public bool AutoResetStats = false;

            [JsonProperty(PropertyName = "Автоматический сброс наигранного времени при вайпе карты")]
            public bool AutoResetPlaytime = false;

            [JsonProperty(PropertyName = "Автоматический сброс истории убийств при вайпе карты")]
            public bool AutoResetKills = false;

            [JsonProperty(PropertyName = "Сохранять историю убийств")]
            public bool LogKills = true;

            [JsonProperty(PropertyName = "Цвет фона (в формате RGBA)")]
            public string OverlayColor = "43 52 58 1.0";

            internal string UIOverlayColor;

            [JsonProperty(PropertyName = "Цвет заголовка (в формате RGBA)")]
            public string TitleColor = "51 60 66 1.0";

            internal string UITitleColor;

            [JsonProperty(PropertyName = "Цвет первого разделителя (в формате RGBA)")]
            public string FirstColorSeparator = "43 52 58 1.0";

            internal string UIFirstColorSeparator;

            [JsonProperty(PropertyName = "Цвет второго разделителя (в формате RGBA)")]
            public string SecondColorSeparator = "47 56 62 1.0";

            internal string UISecondColorSeparator;

            [JsonProperty(PropertyName = "Чат команда для вызова топа")]
            public string ChatCommand = "top";
        }

        private Setting _setting;

        private void InitSetting()
        {
            var defaultSetting = new Setting {ConfigVersion = "0.0.1"};
            try
            {
                _setting = Config.ReadObject<Setting>();
            }
            catch
            {
                string message =
                    $"[{Name}] Файл конфигурации \"{Name}.json\" содержит ошибку и был заменен на стандартный.";
                try
                {
                    Config.Save($"{Config.Filename}.error");
                    Debug.LogError(
                        $"{message} Ошибочная версии файла конфигурации сохранена под названием \"{Name}.json.error\".");
                }
                catch
                {
                    Debug.LogError(message);
                }
            }

            if (_setting == null) _setting = defaultSetting;
            if (_setting.ConfigVersion != defaultSetting.ConfigVersion)
            {
                if (Config.GetEnumerator().MoveNext())
                {
                    Config.Save($"{Config.Filename}.old");
                    Debug.LogWarning(
                        $"[{Name}] Файл конфигурации \"{Name}.json\" обновлен до актуальной версии. Старая версия файла конфигурации сохранена под названием \"{Name}.json.old\".");
                }

                _setting = defaultSetting;
            }

            LoadSetting(defaultSetting);
        }

        private void LoadSetting(Setting defaultSetting)
        {
            ConvertToColorString(defaultSetting.OverlayColor, ref _setting.OverlayColor, ref _setting.UIOverlayColor);
            ConvertToColorString(defaultSetting.TitleColor, ref _setting.TitleColor, ref _setting.UITitleColor);
            ConvertToColorString(defaultSetting.FirstColorSeparator, ref _setting.FirstColorSeparator,
                ref _setting.UIFirstColorSeparator);
            ConvertToColorString(defaultSetting.SecondColorSeparator, ref _setting.SecondColorSeparator,
                ref _setting.UISecondColorSeparator);
            UIOverlay = UIOverlay.Replace("{overlay.color}", _setting.UIOverlayColor)
                .Replace("{title.color}", _setting.UITitleColor);
            SaveSetting();
        }

        private void SaveSetting()
        {
            Config.WriteObject(_setting, true);
        }

        private void ConvertToColorString(string defaultRgba, ref string settingRgba, ref string color)
        {
            if (ConvertToColorString(settingRgba, out color)) return;
            Debug.LogError(
                $"[{Name}] Неверный формат цвета \"{settingRgba}\", используйте формат RGBA. Параметр изменен на \"{defaultRgba}\".");
            settingRgba = defaultRgba;
            ConvertToColorString(defaultRgba, out color);
        }

        private bool ConvertToColorString(string rgba, out string color)
        {
            color = "1 1 1 1";
            var args = rgba.Split(' ');
            if (args.Length != 4) return false;
            byte r, g, b;
            float a;
            if (!byte.TryParse(args[0], out r) || !byte.TryParse(args[1], out g) || !byte.TryParse(args[2], out b) ||
                !float.TryParse(args[3], out a) || a < 0f || a > 1f) return false;
            color = $"{r / 255f} {g / 255f} {b / 255f} {a}";
            return true;
        }

        #endregion

        #region AspectRatio

        private const float OverlayHalf = 0.472223f;

        private class Ratio
        {
            public string MinY;
            public string MaxY;

            public Ratio(float scale)
            {
                MinY = (0.5f - OverlayHalf / scale).ToString("N6");
                MaxY = (0.5f + OverlayHalf / scale).ToString("N6");
            }
        }

        private readonly List<Ratio> _ratioList = new List<Ratio>();

        private void InitAspectRatio()
        {
            _ratioList.Add(new Ratio(1f)); // 16:9
            _ratioList.Add(new Ratio(1.111111f)); // 16:10 == 10 / 9 = 1.111111
            _ratioList.Add(new Ratio(1.333333f)); // 4:3 == (3 * (16 / 4 = 4)) / 9 = 1.333333
            _ratioList.Add(new Ratio(1.422222f)); // 5:4 == (4 * (16 / 5 = 3.2)) / 9 = 1.422222
        }

        #endregion

        #region Variables
        
        private class TopResponse
        {
            public class Data
            {
                public int kills_player;
                public int points;
                public int deaths_player;
                public int playtime;
                public string[] steamData;
            }

            public string status;
            public Data[] data;

            public TopResponse(string response)
            {
                try
                {
                    var obj = JsonConvert.DeserializeObject<TopResponse>(response);
                    status = obj.status;
                    data = obj.data;
                }
                catch
                {
                    status = "error";
                    data = null;
                }
            }
        }

        private class StatsData
        {
            internal string UserId;
            public uint PlayTime;
            public readonly Dictionary<string, int> Kills = new Dictionary<string, int>();
            public readonly Dictionary<string, int> Deaths = new Dictionary<string, int>();
            public readonly Dictionary<string, int> Harvests = new Dictionary<string, int>();

            public void Clear()
            {
                PlayTime = 0u;
                Kills.Clear();
                Deaths.Clear();
                Harvests.Clear();
            }
        }


        private static readonly Oxide.Core.Libraries.Time LibTime = Interface.Oxide.GetLibrary<Oxide.Core.Libraries.Time>();

        private readonly Dictionary<ulong, StatsData> _stats = new Dictionary<ulong, StatsData>();

        private readonly Dictionary<ulong, uint> _joinStamp = new Dictionary<ulong, uint>();
        private readonly Dictionary<string, string> _prefabsToDisplayName = new Dictionary<string, string>();
        private string _logKills = string.Empty;

        private bool _initialized;
        private bool _wipe;

        private const string ApiVersion = "0.0.1";
        
        #endregion

        #region OxideHooks

        [HookMethod("Init")]
        void Init()
        {
            Unsubscribe(nameof(OnPlayerConnected));
            Unsubscribe(nameof(OnPlayerDisconnected));
            Unsubscribe(nameof(OnEntityDeath));
            Unsubscribe(nameof(OnDispenserGather));
            Unsubscribe(nameof(OnCollectiblePickup));
            Unsubscribe(nameof(OnServerSave));
            timer.Once(600f, OnServerInitialized);
        }

        [HookMethod("OnServerInitialized")]
        void OnServerInitialized()
        {
            if (_initialized) return;
            InitSetting();
            InitAspectRatio();
            FindPlugins();
            RegisterMessages();
            timer.Once(5f, PrefabsToDisplayName);
            _initialized = true;
        }

        [HookMethod("OnServerSave")]
        void OnServerSave()
        {
            var timestamp = LibTime.GetUnixTimestamp();
            var json = string.Empty;
            var removeList = new List<ulong>();
            foreach (var pair in _stats)
            {
                var playerId = pair.Key;
                var str = $"{{\"steamID\":\"{pair.Value.UserId}\",\"stats\":[]}},";
                var stats = pair.Value.Kills.Aggregate(string.Empty,
                    (current, i) => current + $"{{\"name\":\"kills_{i.Key}\",\"value\":{i.Value}}},");
                stats += pair.Value.Deaths.Aggregate(string.Empty,
                    (current, i) => current + $"{{\"name\":\"deaths_{i.Key}\",\"value\":{i.Value}}},");
                stats += pair.Value.Harvests.Aggregate(string.Empty,
                        (current, i) => current + $"{{\"name\":\"harvests_{i.Key}\",\"value\":{i.Value}}},")
                    .Replace('.', '_');
                uint time;
                if (_joinStamp.TryGetValue(playerId, out time))
                {
                    stats += $"{{\"name\":\"playtime\",\"value\":{timestamp - time + pair.Value.PlayTime}}},";
                    _joinStamp[playerId] = timestamp;
                    pair.Value.Clear();
                }
                else
                {
                    stats += $"{{\"name\":\"playtime\",\"value\":{pair.Value.PlayTime}}},";
                    removeList.Add(pair.Key);
                }

                json += str.Insert(str.Length - 3, stats.Substring(0, stats.Length - 1));
            }

            if (removeList.Count != 0)
            {
                foreach (var id in removeList) _stats.Remove(id);
            }

            if (json != string.Empty)
            {
                var data = new Dictionary<string, string>
                {
                    {"storeID", RustStore.StoreId},
                    {"serverID", RustStore.ServerId},
                    {"serverKey", RustStore.ServerKey},
                    {"v", $"{ApiVersion}"},
                    {"modules", "statistics"},
                    {"action", "updateData"},
                    {"data", $"[{json.Substring(0, json.Length - 1)}]"}
                };
                WWWRequests.Post(RustStoreUrl, data, OnRequestComplite);
            }

            if (_setting.LogKills && _logKills != string.Empty)
            {
                var data = new Dictionary<string, string>
                {
                    {"storeID", RustStore.StoreId},
                    {"serverID", RustStore.ServerId},
                    {"serverKey", RustStore.ServerKey},
                    {"v", $"{ApiVersion}"},
                    {"modules", "statistics"},
                    {"action", "logKills"},
                    {"data", $"[{_logKills.Substring(0, _logKills.Length - 1)}]"}
                };
                _logKills = string.Empty;
                WWWRequests.Post(RustStoreUrl, data, OnRequestComplite);
            }
        }

        //[HookMethod("Unload")]
        public override void HandleRemovedFromManager(PluginManager manager)
        {
            if (_initialized)
            {
                foreach (var p in BasePlayer.activePlayerList) OnPlayerDisconnected(p);
                UI.Destroy("RustStats.Close");
                UI.Destroy("RustStats.Overlay");
                UI.Destroy("RustStats.Info");
                UI.Destroy("RustStats.Title");
                UI.Destroy("RustStats.Points");
                UI.Destroy("RustStats.Kills");
                UI.Destroy("RustStats.Deaths");
                UI.Destroy("RustStats.Playtime");
                for (var i = 0; i < 10; i++) UI.Destroy($"RustStats.User{i}");
            }

            OnServerSave();
            WWWRequests.DestroyThis();
            base.HandleRemovedFromManager(manager);
        }

        #region ApiPlugins

        private Plugin _pluginAspectRatio;

        private void FindPlugins()
        {
            _pluginAspectRatio = plugins.Find("AspectRatio");
        }

        [HookMethod("OnPluginLoaded")]
        void OnPluginLoaded(Plugin plugin)
        {
            if (plugin.Name != "AspectRatio") return;
            _pluginAspectRatio = plugin;
        }

        [HookMethod("OnPluginUnloaded")]
        void OnPluginUnloaded(Plugin plugin)
        {
            if (_pluginAspectRatio != plugin) return;
            _pluginAspectRatio = null;
        }

        #endregion

        [HookMethod("OnNewSave")]
        void OnNewSave(string filename)
        {
            _wipe = true;
        }

        [HookMethod("OnPlayerConnected")]
        void OnPlayerConnected(BasePlayer player)
        {
            var playerId = player.userID;
            _joinStamp[playerId] = LibTime.GetUnixTimestamp();
            if (!_stats.ContainsKey(playerId)) _stats[playerId] = new StatsData {UserId = playerId.ToString()};
        }

        [HookMethod("OnPlayerDisconnected")]
        void OnPlayerDisconnected(BasePlayer player)
        {
            var playerId = player.userID;
            if (!_stats.ContainsKey(playerId) || !_joinStamp.ContainsKey(playerId)) return;
            _stats[playerId].PlayTime += LibTime.GetUnixTimestamp() - _joinStamp[playerId];
            _joinStamp.Remove(playerId);
        }

        [HookMethod("OnEntityDeath")]
        void OnEntityDeath(BaseCombatEntity entity, HitInfo hitInfo)
        {
            switch (entity.prefabID)
            {
                case 1799741974: // assets/rust.ai/agents/bear/bear.prefab
                {
                    var killer = hitInfo?.Initiator as BasePlayer;
                    if (killer == null || IsBot(killer)) return;
                    AddKill(killer.userID, new[] {"bear", "animal"});
                    return;
                }
                case 502341109: // assets/rust.ai/agents/boar/boar.prefab
                {
                    var killer = hitInfo?.Initiator as BasePlayer;
                    if (killer == null || IsBot(killer)) return;
                    AddKill(killer.userID, new[] {"boar", "animal"});
                    return;
                }
                case 152398164: // assets/rust.ai/agents/chicken/chicken.prefab
                {
                    var killer = hitInfo?.Initiator as BasePlayer;
                    if (killer == null || IsBot(killer)) return;
                    AddKill(killer.userID, new[] {"chicken", "animal"});
                    return;
                }
                case 3880446623: // assets/rust.ai/agents/horse/horse.prefab
                {
                    var killer = hitInfo?.Initiator as BasePlayer;
                    if (killer == null || IsBot(killer)) return;
                    AddKill(killer.userID, new[] {"horse", "animal"});
                    return;
                }
                case 1378621008: // assets/rust.ai/agents/stag/stag.prefab
                {
                    var killer = hitInfo?.Initiator as BasePlayer;
                    if (killer == null || IsBot(killer)) return;
                    AddKill(killer.userID, new[] {"stag", "animal"});
                    return;
                }
                case 2144238755: // assets/rust.ai/agents/wolf/wolf.prefab
                {
                    var killer = hitInfo?.Initiator as BasePlayer;
                    if (killer == null || IsBot(killer)) return;
                    AddKill(killer.userID, new[] {"wolf", "animal"});
                    return;
                }
                case 3029415845: // assets/prefabs/npc/patrol helicopter/patrolhelicopter.prefab
                {
                    var killer = entity.lastAttacker as BasePlayer;
                    if (killer == null || IsBot(killer)) return;
                    AddKill(killer.userID, "helicopter");
                    return;
                }
                case 1456850188: // assets/prefabs/npc/m2bradley/bradleyapc.prefab
                {
                    var killer = entity.lastAttacker as BasePlayer;
                    if (killer == null || IsBot(killer)) return;
                    AddKill(killer.userID, "bradley");
                    return;
                }
                case 4108440852: // assets/prefabs/player/player.prefab
                {
                    var victim = entity as BasePlayer;
                    if (victim == null || IsBot(victim)) return;
                    var victimId = victim.userID;
                    AddDeath(victimId, "total");
                    switch (victim.lastDamage)
                    {
                        case DamageType.Radiation:
                            AddDeath(victimId, "radiation");
                            return;
                        case DamageType.Suicide:
                            AddDeath(victimId, "suicide");
                            return;
                        case DamageType.Fall:
                            AddDeath(victimId, "fall");
                            return;
                    }

                    var initiator = hitInfo?.Initiator;
                    if (initiator != null)
                    {
                        var killer = initiator as BasePlayer;
                        if (killer != null && !IsBot(killer))
                        {
                            var killerId = killer.userID;
                            if (_setting.LogKills)
                                _logKills +=
                                    $"{{\"victim\":\"{victimId}\",\"killer\":\"{killerId}\",\"weapon\":\"{GetWeaponName(killer, hitInfo)}\",\"bone\":\"{hitInfo.boneName}\",\"time\":\"{LibTime.GetUnixTimestamp()}\"}},";
                            AddDeath(victimId, "player");
                            AddKill(killerId, "player");
                            switch (victim.lastDamage)
                            {
                                case DamageType.Arrow:
                                    AddKill(killerId, "arrow");
                                    break;
                                case DamageType.Bullet:
                                    AddKill(killerId, "bullet");
                                    break;
                                case DamageType.Slash:
                                case DamageType.Blunt:
                                case DamageType.Stab:
                                    AddKill(killerId, "melee");
                                    break;
                            }

                            if (victim.IsSleeping()) AddKill(killerId, "sleeping");
                            else if (victim.IsWounded()) AddKill(killerId, "wounded");
                            else if (hitInfo.isHeadshot) AddKill(killerId, "headshot");
                            return;
                        }

                        switch (initiator.prefabID)
                        {
                            case 1799741974: // assets/rust.ai/agents/bear/bear.prefab
                            case 2144238755: // assets/rust.ai/agents/wolf/wolf.prefab
                            case 502341109: // assets/rust.ai/agents/boar/boar.prefab
                                AddDeath(victimId, "animal");
                                return;
                            case 3029415845: // assets/prefabs/npc/patrol helicopter/patrolhelicopter.prefab
                                AddDeath(victimId, "helicopter");
                                return;
                            case 922529517: // assets/prefabs/deployable/bear trap/beartrap.prefab
                            case 3824663394: // assets/prefabs/deployable/barricades/barricade.metal.prefab
                            case 4254045167: // assets/prefabs/deployable/barricades/barricade.wood.prefab
                            case 1202834203: // assets/prefabs/deployable/barricades/barricade.woodwire.prefab
                            case 1463807579: // assets/prefabs/deployable/landmine/landmine.prefab
                            case 1585379529
                                : // assets/prefabs/building/wall.external.high.stone/wall.external.high.stone.prefab
                            case 1745077396
                                : // assets/prefabs/building/wall.external.high.wood/wall.external.high.wood.prefab
                            case 976279966: // assets/prefabs/deployable/floor spikes/spikes.floor.prefab
                            case 3312510084: // assets/prefabs/npc/autoturret/autoturret_deployed.prefab
                            case 4075317686: // assets/prefabs/npc/flame turret/flameturret.deployed.prefab
                            case 1348746224: // assets/prefabs/deployable/single shot trap/guntrap.deployed.prefab
                                AddDeath(victimId, "traps");
                                return;
                        }
                    }

                    return;
                }
                case 966676416: // assets/bundled/prefabs/autospawn/resource/loot/loot-barrel-1.prefab
                case 555882409: // assets/bundled/prefabs/autospawn/resource/loot/loot-barrel-2.prefab
                case 3364121927: // assets/bundled/prefabs/radtown/loot_barrel_1.prefab
                case 3269883781: // assets/bundled/prefabs/radtown/loot_barrel_2.prefab
                case 3279100614: // assets/bundled/prefabs/radtown/loot_trash.prefab
                case 3438187947: // assets/bundled/prefabs/radtown/oil_barrel.prefab
                {
                    var killer = hitInfo?.Initiator as BasePlayer;
                    if (killer == null || IsBot(killer)) return;
                    AddKill(killer.userID, "barrel");
                    return;
                }
                default:
                {
                    var killer = hitInfo?.Initiator as BasePlayer;
                    if (killer == null || IsBot(killer)) return;
                    var name = entity.name;
                    if (name.Contains("assets/prefabs/building"))
                    {
                        if ((entity as BuildingBlock)?.grade == BuildingGrade.Enum.Twigs) return;
                        var killerId = killer.userID;
                        if (killerId == entity.OwnerID) return;
                        AddKill(killerId, "building");
                    }

                    return;
                }
            }
        }

        [HookMethod("OnDispenserGather")]
        void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            var player = entity as BasePlayer;
            if (player == null) return;
            AddHarvest(player.userID, item.info.shortname, item.amount);
        }

        [HookMethod("OnCollectiblePickup")]
        void OnCollectiblePickup(Item item, BasePlayer player)
        {
            AddHarvest(player.userID, item.info.shortname, item.amount);
        }

        [HookMethod("ChatCmdTop")]
        void ChatCmdTop(BasePlayer player, string command, string[] args)
        {
            ShowTop(player, "points", true);
            SendMessage(player, GetLang("CMD.TOP.HELP", player));
        }

        [HookMethod("ConsoleCmdClose")]
        void ConsoleCmdClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            UI.Destroy(player, "RustStats.Close");
            UI.Destroy(player, "RustStats.Overlay");
            UI.Destroy(player, "RustStats.Info");
            UI.Destroy(player, "RustStats.Title");
            UI.Destroy(player, "RustStats.Points");
            UI.Destroy(player, "RustStats.Kills");
            UI.Destroy(player, "RustStats.Deaths");
            UI.Destroy(player, "RustStats.Playtime");
            for (var i = 0; i < 10; i++) UI.Destroy(player, $"RustStats.User{i}");
        }

        [HookMethod("ConsoleCmdSort")]
        void ConsoleCmdSort(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            UI.Destroy(player, "RustStats.Info");
            for (var i = 0; i < 10; i++) UI.Destroy(player, $"RustStats.User{i}");
            var type = arg.cmd.Name.Replace("ruststats.", "");
            if (type == "kills" || type == "deaths") type += "_player";
            ShowTop(player, type);
        }

        [HookMethod("ConsoleCmdResetStats")]
        void ConsoleCmdResetStats(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Player()?.IsAdmin != true) return;
            if (arg.Args?.Length > 0)
            {
                ulong userId;
                if (arg.Args[0].Length != 17 || !ulong.TryParse(arg.Args[0], out userId) || userId < 76561197960265728u)
                {
                    SendMessage(null, GetLang("ERROR.FORMAT.STEAMID"), arg);
                    return;
                }

                WipeStats(arg.Args[0]);
                return;
            }

            WipeStats();
            SendMessage(null, GetLang("SEND.REQUEST.WIPE.STATS"), arg);
        }

        [HookMethod("ConsoleCmdResetPlaytime")]
        void ConsoleCmdResetPlaytime(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Player()?.IsAdmin != true) return;
            if (arg.Args?.Length > 0)
            {
                ulong userId;
                if (arg.Args[0].Length != 17 || !ulong.TryParse(arg.Args[0], out userId) || userId < 76561197960265728u)
                {
                    SendMessage(null, GetLang("ERROR.FORMAT.STEAMID"), arg);
                    return;
                }

                WipePlaytime(arg.Args[0]);
                return;
            }

            WipePlaytime();
            SendMessage(null, GetLang("SEND.REQUEST.WIPE.PLAYTIME"), arg);
        }

        [HookMethod("ConsoleCmdResetKills")]
        void ConsoleCmdResetKills(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Player()?.IsAdmin != true) return;
            if (arg.Args?.Length > 0)
            {
                ulong userId;
                if (arg.Args[0].Length != 17 || !ulong.TryParse(arg.Args[0], out userId) || userId < 76561197960265728u)
                {
                    SendMessage(null, GetLang("ERROR.FORMAT.STEAMID"), arg);
                    return;
                }

                WipeKills(arg.Args[0]);
                return;
            }

            WipeKills();
            SendMessage(null, GetLang("SEND.REQUEST.WIPE.KILLS"), arg);
        }

        #endregion

        #region CustomMethods

        #region RustStore

        private const string RustStoreUrl = "https://store-api.moscow.ovh/index.php";

        private struct RustStore
        {
            public static string StoreId;
            public static string ServerId;
            public static string ServerKey;
        }

        private void CheckRustStore()
        {
            try
            {
                var plugin = plugins.Find("RustStore");
                if (plugin == null)
                {
                    if (FindUnloadedPlugin("RustStore")) throw new Exception();
                    Debug.LogError($"[{Name}] Для работы данного плагина необходимо установить плагин \"RustStore\".");
                    return;
                }

                RustStore.StoreId = plugin.Config.Get<string>("номер магазина");
                RustStore.ServerId = plugin.Config.Get<string>("номер сервера");
                RustStore.ServerKey = plugin.Config.Get<string>("ключ сервера");
                var data = new Dictionary<string, string>
                {
                    {"storeID", RustStore.StoreId},
                    {"serverID", RustStore.ServerId},
                    {"serverKey", RustStore.ServerKey},
                    {"modules", "servers"},
                    {"action", "checkAuth"},
                    {"plugin", "rustStats"}
                };
                WWWRequests.Post(RustStoreUrl, data, (response, error) =>
                {
                    var responseObj = new WwwMoscowOvh(response);
                    if (responseObj.Status == "error")
                    {
                        if (responseObj.Message == "unload")
                        {
                            Debug.LogError(
                                $"[{Name}] Статистика игроков отключена в панели управления магазином для данного сервера.");
                            Interface.Oxide.UnloadPlugin(Name);
                            return;
                        }

                        if (!string.IsNullOrEmpty(error))
                        {
                            Debug.LogError(
                                $"[{Name}] Не удалось подключится к плагину \"RustStore\", проверьте настройки \"RustStore.json\".");
                            Interface.Oxide.UnloadPlugin(Name);
                            return;
                        }
                    }

                    Subscribe(nameof(OnPlayerConnected));
                    Subscribe(nameof(OnPlayerDisconnected));
                    Subscribe(nameof(OnEntityDeath));
                    Subscribe(nameof(OnDispenserGather));
                    Subscribe(nameof(OnCollectiblePickup));
                    Subscribe(nameof(OnServerSave));
                    RegisterCommands();
                    foreach (var p in BasePlayer.activePlayerList) OnPlayerConnected(p);
                    if (!_wipe) return;
                    if (_setting.AutoResetStats) WipeStats();
                    if (_setting.AutoResetPlaytime) WipePlaytime();
                    if (_setting.AutoResetKills) WipeKills();
                    _wipe = false;
                });
            }
            catch
            {
                Debug.LogError(
                    $"[{Name}] Не удалось подключится к плагину \"RustStore\", проверьте настройки \"RustStore.json\".");
                Interface.Oxide.UnloadPlugin($"{Name}");
            }
        }

        #endregion

        private void RegisterCommands()
        {
            var command = Interface.Oxide.GetLibrary<Oxide.Game.Rust.Libraries.Command>();
            command.AddChatCommand(_setting.ChatCommand, this, "ChatCmdTop");
            command.AddConsoleCommand("ruststats.close", this, "ConsoleCmdClose");
            command.AddConsoleCommand("ruststats.points", this, "ConsoleCmdSort");
            command.AddConsoleCommand("ruststats.kills", this, "ConsoleCmdSort");
            command.AddConsoleCommand("ruststats.deaths", this, "ConsoleCmdSort");
            command.AddConsoleCommand("ruststats.playtime", this, "ConsoleCmdSort");
            command.AddConsoleCommand("ruststats.resetstats", this, "ConsoleCmdResetStats");
            command.AddConsoleCommand("ruststats.resetplaytime", this, "ConsoleCmdResetPlaytime");
            command.AddConsoleCommand("ruststats.resetkills", this, "ConsoleCmdResetKills");
        }

        private void RegisterMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                {"UI.AVATAR", "<color=#c2c5c7><size=12>АВАТАР</size></color>"},
                {"UI.USER", "<color=#c2c5c7><size=12>ПОЛЬЗОВАТЕЛЬ</size></color>"},
                {"UI.POINTS", "<color=#c2c5c7><size=12>ОЧКИ</size></color>"},
                {"UI.KILLS", "<color=#c2c5c7><size=12>УБИЙСТВА</size></color>"},
                {"UI.DEATHS", "<color=#c2c5c7><size=12>СМЕРТИ</size></color>"},
                {"UI.PLAYTIME", "<color=#c2c5c7><size=12>ВРЕМЯ ИГРЫ</size></color>"},
                {"UI.LOAD.DATA", "<color=#c2c5c7><size=20>Загрузка данных...</size></color>"},
                {"UI.ERROR.LOAD.DATA", "<color=#c2c5c7><size=20>Ошибка при загрузке данных...</size></color>"},
                {"DAYS.DECLENSION1", "день"},
                {"DAYS.DECLENSION2", "дня"},
                {"DAYS.DECLENSION3", "дней"},
                {"HOURS.DECLENSION1", "час"},
                {"HOURS.DECLENSION2", "часа"},
                {"HOURS.DECLENSION3", "часов"},
                {"MINUTES.DECLENSION1", "минута"},
                {"MINUTES.DECLENSION2", "минуты"},
                {"MINUTES.DECLENSION3", "минут"},
                {"SECONDS.DECLENSION1", "секунда"},
                {"SECONDS.DECLENSION2", "секунды"},
                {"SECONDS.DECLENSION3", "секунд"},
                {"SEND.REQUEST.WIPE.STATS", "Отправлен запрос на сброс основной статистики."},
                {"SEND.REQUEST.WIPE.PLAYTIME", "Отправлен запрос на сброс игрового времени."},
                {"SEND.REQUEST.WIPE.KILLS", "Отправлен запрос на сброс истории убийств."},
                {"ERROR.FORMAT.STEAMID", "Некорретный формат SteamID64."},
                {"CMD.TOP.HELP", "Нажмите на необходимый параметр в Топе чтобы изменить сортировку игроков."}
            }, this);
        }

        private void ShowTop(BasePlayer player, string type, bool showOverlay = false)
        {
            if (showOverlay)
            {
                var ratio = _ratioList[(int) (_pluginAspectRatio?.CallHook("ApiGetRatio", player.userID) ?? 0)];
                UI.Add(player,
                    string.Format(UIOverlay, ratio.MinY, ratio.MaxY, GetLang("UI.AVATAR", player),
                        GetLang("UI.USER", player), GetLang("UI.POINTS", player), GetLang("UI.KILLS", player),
                        GetLang("UI.DEATHS", player), GetLang("UI.PLAYTIME", player)));
            }

            UI.Add(player, string.Format(UIInfo, GetLang("UI.LOAD.DATA", player)));
            var data = new Dictionary<string, string>
            {
                {"storeID", RustStore.StoreId},
                {"serverID", RustStore.ServerId},
                {"serverKey", RustStore.ServerKey},
                {"modules", "statistics"},
                {"action", "getTop"},
                {"sortIndex", type} // points || deaths_player || kills_player || playtime
            };
            WWWRequests.Post("https://store-api.moscow.ovh/index.php", data, (response, error) =>
            {
                var top = new TopResponse(response);
                if (top.status == "error" || !string.IsNullOrEmpty(error))
                {
                    UI.Destroy(player, "RustStats.Info");
                    UI.Add(player, string.Format(UIInfo, GetLang("UI.ERROR.LOAD.DATA", player)));
                    return;
                }

                var json = string.Empty;
                var maxY = 0.936274f;
                var offset = 0.092156f;
                var color = true;
                for (var i = 0; i < top.data.Length; i++)
                {
                    var user = top.data[i];
                    var minY = maxY - offset;
                    json += string.Format(UIUser.Replace("{num}", i.ToString()),
                        color ? _setting.UIFirstColorSeparator : _setting.UISecondColorSeparator,
                        minY,
                        maxY,
                        user.steamData[1],
                        EncodingBase64.Decode(user.steamData[0]).Replace("\"", "'"),
                        user.points,
                        user.kills_player,
                        user.deaths_player,
                        ConvertToStringTime((uint) user.playtime));
                    color = !color;
                    maxY = minY;
                }

                UI.Destroy(player, "RustStats.Info");
                UI.Add(player, $"[{json}]");
            });
        }

        public static class EncodingBase64 // v1.0
        {
            public static string Encode(string text)
            {
                var textAsBytes = Encoding.UTF8.GetBytes(text);
                return Convert.ToBase64String(textAsBytes);
            }

            public static string Decode(string encodedText)
            {
                var textAsBytes = Convert.FromBase64String(encodedText);
                return Encoding.UTF8.GetString(textAsBytes);
            }
        }

        private void OnRequestComplite(string response, string error)
        {
            var responseObj = new WwwMoscowOvh(response);
            if (responseObj.Status != "error" && string.IsNullOrEmpty(error)) return;
            switch (responseObj.Message)
            {
                case "reload":
                    Interface.Oxide.ReloadPlugin(Name);
                    break;
                case "unload":
                    Interface.Oxide.UnloadPlugin(Name);
                    break;
                default:
                {
                    var data = new Dictionary<string, string>
                    {
                        {"storeID", RustStore.StoreId},
                        {"serverID", RustStore.ServerId},
                        {"serverKey", RustStore.ServerKey},
                        {"v", $"{ApiVersion}"},
                        {"modules", "statistics"},
                        {"action", "errorLog"},
                        {"data", $"{response}{error}"}
                    };
                    WWWRequests.Post(RustStoreUrl, data);
                    break;
                }
            }
        }

        private void WipeStats(string userIdString = null)
        {
            var data = new Dictionary<string, string>
            {
                {"storeID", RustStore.StoreId},
                {"serverID", RustStore.ServerId},
                {"serverKey", RustStore.ServerKey},
                {"v", $"{ApiVersion}"},
                {"modules", "statistics"},
                {"action", "resetStatistics"}
            };
            if (!string.IsNullOrEmpty(userIdString)) data.Add("steamID", userIdString);
            WWWRequests.Post(RustStoreUrl, data, OnRequestComplite);
        }

        private void WipePlaytime(string userIdString = null)
        {
            var data = new Dictionary<string, string>
            {
                {"storeID", RustStore.StoreId},
                {"serverID", RustStore.ServerId},
                {"serverKey", RustStore.ServerKey},
                {"v", $"{ApiVersion}"},
                {"modules", "statistics"},
                {"action", "resetPlaytime"}
            };
            if (!string.IsNullOrEmpty(userIdString)) data.Add("steamID", userIdString);
            WWWRequests.Post(RustStoreUrl, data, OnRequestComplite);
            foreach (var statsData in _stats.Values) statsData.PlayTime = 0;
        }

        private void WipeKills(string userIdString = null)
        {
            var data = new Dictionary<string, string>
            {
                {"storeID", RustStore.StoreId},
                {"serverID", RustStore.ServerId},
                {"serverKey", RustStore.ServerKey},
                {"v", $"{ApiVersion}"},
                {"modules", "statistics"},
                {"action", "resetKills"}
            };
            if (!string.IsNullOrEmpty(userIdString)) data.Add("steamID", userIdString);
            WWWRequests.Post(RustStoreUrl, data, OnRequestComplite);
        }

        private void AddKill(ulong playerId, string type)
        {
            StatsData data;
            if (!_stats.TryGetValue(playerId, out data))
                data = _stats[playerId] = new StatsData {UserId = playerId.ToString()};
            var kills = data.Kills;
            int amount;
            kills.TryGetValue(type, out amount);
            kills[type] = amount + 1;
        }

        private void AddKill(ulong playerId, string[] types)
        {
            StatsData data;
            if (!_stats.TryGetValue(playerId, out data))
                data = _stats[playerId] = new StatsData {UserId = playerId.ToString()};
            var kills = data.Kills;
            foreach (var type in types)
            {
                int amount;
                kills.TryGetValue(type, out amount);
                kills[type] = amount + 1;
            }
        }

        private void AddDeath(ulong playerId, string type)
        {
            StatsData data;
            if (!_stats.TryGetValue(playerId, out data))
                data = _stats[playerId] = new StatsData {UserId = playerId.ToString()};
            var webDeaths = data.Deaths;
            int amount;
            webDeaths.TryGetValue(type, out amount);
            webDeaths[type] = amount + 1;
        }

        private void AddHarvest(ulong playerId, string type, int itemAmount)
        {
            StatsData data;
            if (!_stats.TryGetValue(playerId, out data))
                data = _stats[playerId] = new StatsData {UserId = playerId.ToString()};
            var harvests = data.Harvests;
            int amount;
            harvests.TryGetValue(type, out amount);
            harvests[type] = itemAmount + amount;
        }

        private void PrefabsToDisplayName()
        {
            foreach (var item in ItemManager.itemList)
            {
                var name = item.displayName.english;

                if (item.worldModelPrefab.isValid && (item.category == ItemCategory.Ammunition ||
                                                      item.category == ItemCategory.Weapon ||
                                                      item.category == ItemCategory.Traps ||
                                                      item.category == ItemCategory.Tool))
                {
                    _prefabsToDisplayName[
                        GetFileName(item.worldModelPrefab.resourcePath).Replace(".worldmodel.prefab", string.Empty)
                            .Replace("_worldmodel.prefab", string.Empty)] = name;
                }

                var mod = item.itemMods.FirstOrDefault(m => m is ItemModEntity) as ItemModEntity;
                if (mod?.entityPrefab.isValid == true)
                {
                    _prefabsToDisplayName[
                        GetFileName(mod.entityPrefab.resourcePath).Replace(".entity", string.Empty)
                            .Replace(".prefab", string.Empty).Replace(".weapon", string.Empty)] = name;
                }

                var projectile = item.GetComponent<ItemModProjectile>();
                if (projectile?.projectileObject.isValid == true)
                {
                    _prefabsToDisplayName[
                        GetFileName(projectile.projectileObject.resourcePath).Replace(".projectile", string.Empty)
                            .Replace(".prefab", string.Empty)] = name;
                }
            }

            CheckRustStore();
        }

        private string GetWeaponName(BasePlayer killer, HitInfo hitInfo)
        {
            var prefab = hitInfo.WeaponPrefab?.LookupPrefab().name;
            if (string.IsNullOrEmpty(prefab))
            {
                var item = hitInfo.Weapon?.GetItem() ?? killer.GetActiveItem();
                prefab = item?.info.worldModelPrefab.isValid == true ? item.info.worldModelPrefab.resourcePath : "none";
            }

            prefab = new StringBuilder(GetFileName(prefab)).Replace(".deployed", string.Empty)
                .Replace(".entity", string.Empty).Replace(".worldmodel", string.Empty)
                .Replace("_worldmodel", string.Empty).Replace(".weapon", string.Empty).Replace(".prefab", string.Empty)
                .ToString();
            string name;
            if (!_prefabsToDisplayName.TryGetValue(prefab, out name)) name = "Unknown Weapon";
            return name;
        }

        private bool FindUnloadedPlugin(string pluginName)
        {
            var array = plugins.PluginManager.GetPlugins().Where(pl => !pl.IsCorePlugin).ToArray();
            var stringSet = new HashSet<string>(array.Select(pl => pl.Name));
            return (from pluginLoader in Interface.Oxide.GetPluginLoaders()
                from key in pluginLoader.ScanDirectory(Interface.Oxide.PluginDirectory).Except(stringSet)
                where key == pluginName && !pluginLoader.PluginErrors.ContainsKey(key)
                select pluginLoader).Any();
        }

        public static string GetFileName(string path)
        {
            if (path != null)
            {
                int length = path.Length;
                int index = length;
                while (--index >= 0)
                {
                    char ch = path[index];
                    if ((int)ch == (int)Path.DirectorySeparatorChar || (int)ch == (int)Path.AltDirectorySeparatorChar || (int)ch == (int)Path.VolumeSeparatorChar)
                        return path.Substring(index + 1, length - index - 1);
                }
            }
            return path;
        }

        #endregion

        #region Utils

        private bool IsBot(BasePlayer player)
        {
            return player.userID < 76561197960265728u;
        }

        private void SendMessage(BasePlayer player, string message, ConsoleSystem.Arg arg = null,
            ulong index = 0)
        {
            if (arg == null) ConsoleNetwork.SendClientCommand(player.net.connection, "chat.add", 0, index, message);
            else arg.ReplyWith(message);
        }

        private string GetLang(string key, BasePlayer player = null)
        {
            return lang.GetMessage(key, this, player?.UserIDString);
        }

        private string ConvertToStringTime(uint seconds, BasePlayer player = null) // v1.0
        {
            if (seconds == 0) return "0 " + GetLang("SECONDS.DECLENSION3", player);
            var span = TimeSpan.FromSeconds(seconds).Duration();
            var i = 0;
            var formatted = string.Empty;
            if (span.Days != 0)
            {
                formatted += FormatNum(span.Days, GetLang("DAYS.DECLENSION1", player),
                    GetLang("DAYS.DECLENSION2", player), GetLang("DAYS.DECLENSION3", player)) + ", ";
                i++;
            }

            if (span.Hours != 0)
            {
                formatted += FormatNum(span.Hours, GetLang("HOURS.DECLENSION1", player),
                    GetLang("HOURS.DECLENSION2", player), GetLang("HOURS.DECLENSION3", player));
                if (++i == 2) return formatted;
                formatted += ", ";
            }

            if (span.Minutes != 0)
            {
                formatted += FormatNum(span.Minutes, GetLang("MINUTES.DECLENSION1", player),
                    GetLang("MINUTES.DECLENSION2", player), GetLang("MINUTES.DECLENSION3", player));
                if (++i == 2) return formatted;
                formatted += ", ";
            }

            if (span.Seconds == 0) return formatted.Substring(0, formatted.Length - 2);
            return formatted + FormatNum(span.Seconds, GetLang("SECONDS.DECLENSION1", player),
                GetLang("SECONDS.DECLENSION2", player), GetLang("SECONDS.DECLENSION3", player));
        }

        private static string FormatNum(int num, string first, string second, string third) // v1.0
        {
            if (num == 0) return string.Empty;
            var formatted = num + " ";
            if (num > 100) num = num % 100;
            if (num > 9 && num < 21) return formatted + third;
            switch (num % 10)
            {
                case 1: return formatted + first;
                case 2:
                case 3:
                case 4: return formatted + second;
                default: return formatted + third;
            }
        }

        public class WWWRequests // v1.5
        {
            private static readonly HashSet<UnityWebRequest> ActiveRequests = new HashSet<UnityWebRequest>();
            public static int ActiveRequestsCount => ActiveRequests.Count;

            public static void Get(string url, Action<string, string> onRequestComplete = null)
            {
                Rust.Global.Runner.StartCoroutine(WaitForRequest(UnityWebRequest.Get(url), onRequestComplete));
            }

            public static void Post(string url, Dictionary<string, string> data = null, Action<string, string> onRequestComplete = null)
            {
                if (data?.Count > 0)
                {
                    Rust.Global.Runner.StartCoroutine(WaitForRequest(UnityWebRequest.Post(url, data), onRequestComplete));
                }
                else Get(url, onRequestComplete);
            }

            private static IEnumerator WaitForRequest(UnityWebRequest www, Action<string, string> onRequestComplete = null)
            {
                ActiveRequests.Add(www);
                yield return www.SendWebRequest();
                if (ActiveRequests.Remove(www)) onRequestComplete?.Invoke(www.downloadHandler.text, www.error);
            }

            public static void DestroyThis(bool requestsDispose = false)
            {
                if (requestsDispose) foreach (var www in ActiveRequests) www?.Dispose();
                ActiveRequests.Clear();
            }
        }

        public class WwwMoscowOvh // v1.3
        {
            [JsonProperty(PropertyName = "status")]
            public string Status = string.Empty;
            [JsonProperty(PropertyName = "message")]
            public string Message = string.Empty;
            internal bool IsConvert;

            public WwwMoscowOvh() { }

            public WwwMoscowOvh(string text)
            {
                try
                {
                    var obj = JsonConvert.DeserializeObject<WwwMoscowOvh>(text);
                    Status = obj.Status;
                    Status = obj.Status;
                    IsConvert = true;
                }
                catch
                {
                    Status = "error";
                    Status = text;
                }
            }
        }

        #endregion

        #region UI

        private static class UI
        {
            public static void Add(BasePlayer player, string json) =>
                CommunityEntity.ServerInstance.ClientRPCEx(new SendInfo(player.net.connection), null, "AddUI", json);

            public static void Add(string json) =>
                CommunityEntity.ServerInstance.ClientRPCEx(new SendInfo(Net.sv.connections), null, "AddUI", json);

            public static void Destroy(BasePlayer player, string name) =>
                CommunityEntity.ServerInstance.ClientRPCEx(new SendInfo(player.net.connection), null, "DestroyUI",
                    name);

            public static void Destroy(string name) =>
                CommunityEntity.ServerInstance.ClientRPCEx(new SendInfo(Net.sv.connections), null, "DestroyUI", name);
        }

        private string UIOverlay = @"[
    {{
		""name"":""RustStats.Close"",
		""parent"":""Overlay"",
		""components"":
		[
			{{
				""type"":""UnityEngine.UI.Button"",
				""command"":""ruststats.close"",
				""color"":""0 0 0 0"",
				""imagetype"":""Tiled""
			}},
			{{
				""type"":""RectTransform"",
			    ""anchormin"":""0 0"",
			    ""anchormax"":""1 1""
			}},
			{{
                ""type"":""NeedsCursor""
			}}
		]
	}},
    {{
	    ""name"":""RustStats.Overlay"",
	    ""parent"":""Overlay"",
	    ""components"":
	    [
		    {{
			    ""type"":""UnityEngine.UI.Image"",
			    ""color"":""{overlay.color}""
		    }},
		    {{
			    ""type"":""RectTransform"",
			    ""anchormin"":""0.3164 {0}"",
			    ""anchormax"":""0.6836 {1}""
		    }}
	    ]
    }},
    {{
	    ""name"":""RustStats.Title"",
	    ""parent"":""RustStats.Overlay"",
	    ""components"":
	    [
		    {{
			    ""type"":""UnityEngine.UI.Image"",
			    ""color"":""{title.color}""
		    }},
		    {{
			    ""type"":""RectTransform"",
			    ""anchormin"":""0.0213 0.9363"",
			    ""anchormax"":""0.9787 0.9853""
		    }}
	    ]
    }},
    {{
	    ""parent"":""RustStats.Title"",
	    ""components"":
	    [
		    {{
			    ""type"":""UnityEngine.UI.Text"",
                ""align"":""MiddleLeft"",
                ""text"":""{2}""
		    }},
		    {{
			    ""type"":""RectTransform"",
			    ""anchormin"":""0.0222 0"",
			    ""anchormax"":""0.1170 1""
		    }}
	    ]
    }},
    {{
	    ""parent"":""RustStats.Title"",
	    ""components"":
	    [
		    {{
			    ""type"":""UnityEngine.UI.Text"",
                ""align"":""MiddleLeft"",
                ""text"":""{3}""
		    }},
		    {{
			    ""type"":""RectTransform"",
			    ""anchormin"":""0.1614 0"",
			    ""anchormax"":""0.3462 1""
		    }}
	    ]
    }},
    {{
		""name"":""RustStats.Points"",
		""parent"":""RustStats.Title"",
		""components"":
		[
			{{
				""type"":""UnityEngine.UI.Button"",
				""command"":""ruststats.points"",
				""color"":""0 0 0 0"",
				""imagetype"":""Tiled""
			}},
			{{
				""type"":""RectTransform"",
			    ""anchormin"":""0.3904 0"",
			    ""anchormax"":""0.4643 1""
			}}
		]
	}},
	{{
		""parent"":""RustStats.Points"",
		""components"":
		[
			{{
			    ""type"":""UnityEngine.UI.Text"",
                ""align"":""MiddleLeft"",
                ""text"":""{4}""
		    }},
			{{
				""type"":""RectTransform"",
				""anchormin"":""0 0"",
				""anchormax"":""1 1""
			}}
		]
	}},
    {{
		""name"":""RustStats.Kills"",
		""parent"":""RustStats.Title"",
		""components"":
		[
			{{
				""type"":""UnityEngine.UI.Button"",
				""command"":""ruststats.kills"",
				""color"":""0 0 0 0"",
				""imagetype"":""Tiled""
			}},
			{{
				""type"":""RectTransform"",
			    ""anchormin"":""0.5087 0"",
			    ""anchormax"":""0.6342 1""
			}}
		]
	}},
	{{
		""parent"":""RustStats.Kills"",
		""components"":
		[
			{{
			    ""type"":""UnityEngine.UI.Text"",
                ""align"":""MiddleLeft"",
                ""text"":""{5}""
		    }},
			{{
				""type"":""RectTransform"",
				""anchormin"":""0 0"",
				""anchormax"":""1 1""
			}}
		]
	}},
    {{
		""name"":""RustStats.Deaths"",
		""parent"":""RustStats.Title"",
		""components"":
		[
			{{
				""type"":""UnityEngine.UI.Button"",
				""command"":""ruststats.deaths"",
				""color"":""0 0 0 0"",
				""imagetype"":""Tiled""
			}},
			{{
				""type"":""RectTransform"",
			    ""anchormin"":""0.6786 0"",
			    ""anchormax"":""0.7808 1""
			}}
		]
	}},
	{{
		""parent"":""RustStats.Deaths"",
		""components"":
		[
			{{
			    ""type"":""UnityEngine.UI.Text"",
                ""align"":""MiddleLeft"",
                ""text"":""{6}""
		    }},
			{{
				""type"":""RectTransform"",
				""anchormin"":""0 0"",
				""anchormax"":""1 1""
			}}
		]
	}},
    {{
		""name"":""RustStats.Playtime"",
		""parent"":""RustStats.Title"",
		""components"":
		[
			{{
				""type"":""UnityEngine.UI.Button"",
				""command"":""ruststats.playtime"",
				""color"":""0 0 0 0"",
				""imagetype"":""Tiled""
			}},
			{{
				""type"":""RectTransform"",
			    ""anchormin"":""0.8252 0"",
			    ""anchormax"":""0.9778 1""
			}}
		]
	}},
	{{
		""parent"":""RustStats.Playtime"",
		""components"":
		[
			{{
			    ""type"":""UnityEngine.UI.Text"",
                ""align"":""MiddleLeft"",
                ""text"":""{7}""
		    }},
			{{
				""type"":""RectTransform"",
				""anchormin"":""0 0"",
				""anchormax"":""1 1""
			}}
		]
	}}
    ]";

        private string UIUser = @"
    {{
	    ""name"":""RustStats.User{num}"",
	    ""parent"":""RustStats.Overlay"",
	    ""components"":
	    [
		    {{
			    ""type"":""UnityEngine.UI.Image"",
			    ""color"":""{0}""
		    }},
		    {{
			    ""type"":""RectTransform"",
			    ""anchormin"":""0.0213 {1}"",
			    ""anchormax"":""0.9787 {2}""
		    }}
	    ]
    }},
    {{
		""parent"":""RustStats.User{num}"",
		""components"":
		[
			{{
				""sprite"":""assets/content/textures/generic/fulltransparent.tga"",
				""type"":""UnityEngine.UI.RawImage"",
				""imagetype"":""Tiled"",
				""url"":""{3}""
			}},
			{{
				""type"":""RectTransform"",
			    ""anchormin"":""0.0222 0.1595"",
			    ""anchormax"":""0.1170 0.8405""
			}}
		]
	}},
    {{
	    ""parent"":""RustStats.User{num}"",
	    ""components"":
	    [
		    {{
			    ""type"":""UnityEngine.UI.Text"",
                ""align"":""MiddleLeft"",
				""font"":""robotocondensed-regular.ttf"",
                ""text"":""<size=12>{4}</size>""
		    }},
		    {{
			    ""type"":""RectTransform"",
			    ""anchormin"":""0.1614 0.1595"",
			    ""anchormax"":""0.3462 0.8405""
		    }}
	    ]
    }},
    {{
	    ""parent"":""RustStats.User{num}"",
	    ""components"":
	    [
		    {{
			    ""type"":""UnityEngine.UI.Text"",
                ""align"":""MiddleLeft"",
				""font"":""robotocondensed-regular.ttf"",
                ""text"":""<size=12>{5}</size>""
		    }},
		    {{
			    ""type"":""RectTransform"",
			    ""anchormin"":""0.3904 0.1595"",
			    ""anchormax"":""0.4643 0.8405""
		    }}
	    ]
    }},
    {{
	    ""parent"":""RustStats.User{num}"",
	    ""components"":
	    [
		    {{
			    ""type"":""UnityEngine.UI.Text"",
                ""align"":""MiddleLeft"",
				""font"":""robotocondensed-regular.ttf"",
                ""text"":""<size=12>{6}</size>""
		    }},
		    {{
			    ""type"":""RectTransform"",
			    ""anchormin"":""0.5087 0.1595"",
			    ""anchormax"":""0.6342 0.8405""
		    }}
	    ]
    }},
    {{
	    ""parent"":""RustStats.User{num}"",
	    ""components"":
	    [
		    {{
			    ""type"":""UnityEngine.UI.Text"",
                ""align"":""MiddleLeft"",
				""font"":""robotocondensed-regular.ttf"",
                ""text"":""<size=12>{7}</size>""
		    }},
		    {{
			    ""type"":""RectTransform"",
			    ""anchormin"":""0.6786 0.1595"",
			    ""anchormax"":""0.7808 0.8405""
		    }}
	    ]
    }},
    {{
	    ""parent"":""RustStats.User{num}"",
	    ""components"":
	    [
		    {{
			    ""type"":""UnityEngine.UI.Text"",
                ""align"":""MiddleLeft"",
				""font"":""robotocondensed-regular.ttf"",
                ""text"":""<size=12>{8}</size>""
		    }},
		    {{
			    ""type"":""RectTransform"",
			    ""anchormin"":""0.8252 0.1595"",
			    ""anchormax"":""0.9778 0.8405""
		    }}
	    ]
    }},";

        private string UIInfo = @"[
    {{
        ""name"":""RustStats.Info"",
		""parent"":""RustStats.Overlay"",
		""components"":
	    [
		    {{
			    ""type"":""UnityEngine.UI.Text"",
                ""align"":""MiddleCenter"",
                ""text"":""{0}""
		    }},
		    {{
			    ""type"":""RectTransform"",
			    ""anchormin"":""0 0"",
			    ""anchormax"":""1 1""
		    }}
	    ]
    }}
    ]";

        #endregion
    }
}