using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Assets.Features.Hud;
using BepInEx;
using HarmonyLib;
using aag.Natives;
using aag.Natives.Lib.Primitives;
using Assets;
using Assets.Features.Core;
using Assets.Features.Gameplay;
using Assets.Api;
using aag.Natives.Lib.Matchmaking.Common;
using aag.Natives.Api;
using aag.Natives.Lib.ViewObjects;
using aag.Natives.Lib.Guilds;
using aag.Natives.Lib.Misc;
using Newtonsoft.Json;
using UnityEngine.Networking.Match;
using aag.Natives.Lib.Matchmaking.RatingCalculators;
using System.Net.Http;
using System.Text;
using System.Net.Sockets;
using System.IO;
using aag.Natives.Lib.Networking.Messages;
using BepInEx.Configuration;
using aag.Natives.Lib.Properties;
using Assets.Features.Dev;
using System.Text.RegularExpressions;

namespace Logless
{
    using static Logless.Plugin;
    using P = Plugin;

    [BepInProcess("Legion TD 2.exe")]
    [BepInPlugin("UnitTracker", "UnitTracker", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        private readonly Assembly _assembly = Assembly.GetExecutingAssembly();
        private readonly Harmony _harmony = new("UnitTracker");
        private Stopwatch sp = new Stopwatch();
        private bool _registered = false;
        private int waveNumber = 0;
        private List<LTDPlayer> lTDPlayers = new List<LTDPlayer>();
        private List<Unit> units = new List<Unit>();
        private List<Recceived> mercenaries = new List<Recceived>();
        private HttpClient _client = new HttpClient();
        private bool _configured = false;
        private bool _waveSet = false;
        private ConfigEntry<string> configUrl;
        private ConfigEntry<string> configJwt;
        private ConfigEntry<int> configStreamDelay;
        private int maxUnitsSeen = 0;
        private bool _shouldPost = false;
        private string matchUUID = "";
        private Queue<QueuedItem> _queue = new Queue<QueuedItem>();
        private List<Leaks> leaks = new List<Leaks>();
        private int retries = 0;
        public void Awake() {
            configUrl = Config.Bind("General", "UpdateURL", "https://ltd2.krettur.no/v2/update", "HTTPS endpoint to post on waveStarted event, default is the extension used by the Twitch Overlay.");
            configJwt = Config.Bind("General", "JWT", "", "JWT to authenticate your data, reach out to @bosen in discord to get your token for the Twitch Overlay.");
            configStreamDelay = Config.Bind("General", "StreamDelay", 0, "Delay before data is pushed, in seconds. Set this equal to the delay configued in OBS / Stream Labs to prevent the overlay showing information from the future");;

            if (String.IsNullOrEmpty(configJwt.Value))
            {
                Console.WriteLine("Missing JWT to send data, skipping patching.");
                return;
            }
            if (String.IsNullOrEmpty(configUrl.Value))
            {
                Console.WriteLine("Missing target URL, skipping patching.");
                return;
            }

            try
            {
                Patch();
                this._configured = true;
                _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", configJwt.Value);
            }
            catch (Exception e)
            {
                Logger.LogError($"Error while injecting or patching UnitTracker: {e}");
                throw;
            }
            Logger.LogInfo($"Plugin {"UnitTracker"} is loaded!");
            sp.Start();
        }

        private void ConfigStreamDelay_SettingChanged(object sender, EventArgs e)
        {
            throw new NotImplementedException();
        }

        private bool fetchAndPostWaveStartedInfo(bool force = false)
        {
            List<MythiumRecceived> mythium = new List<MythiumRecceived>();
            try
            {
                foreach (ushort p in PlayerApi.GetPlayingPlayers())
                {
                    Console.WriteLine("Fetching for player " + p);
                    LTDPlayer player = this.lTDPlayers.Find(pl => pl.player == p);

                    Dictionary<IntVector2, Scoreboard.ScoreboardGridData> data = Scoreboard.GetGridData(p);

                    

                    if(data.Count < 1)
                    {
                        throw new Exception("Nope, no units for this bad boi");
                    }
                    Console.WriteLine("Found " + data.Count + "Entries... adding them to the list");
                    foreach (IntVector2 key in data.Keys)
                    {
                        int index = units.FindIndex(u => u.player == p && u.x == key.x && u.y == key.y);
                        if (index == -1)
                        {
                            units.Add(new Unit(key.x, key.y, data[key].UnitType, data[key].Image, player.player));
                        }
                    }

                    List<string> mercs = Assets.States.Components.MercenaryIconHandler.GetMercenaryIconsReceived(p, this.waveNumber);

                    foreach (string merc in mercs)
                    {
                        int index = this.mercenaries.FindIndex(r => r.player == player.player && r.image == merc);

                        if (index != -1)
                        {
                            this.mercenaries[index].count++;
                        }
                        else
                        {
                            this.mercenaries.Add(new Recceived(merc, player.player, 1));
                        }
                    }

                    mythium.Add(new MythiumRecceived(Snapshot.PlayerProperties[p].MythiumReceivedPerWave[this.waveNumber], p));
                }
            }
            catch
            {
                
                if (!force)
                {
                    Console.WriteLine("Error fetching scoreboard, reattempting next update.");
                    return false;
                }
                else
                {
                    Console.WriteLine("Error fetching data, but proceeding since the treshold is reached.");
                }
            }
            

            if (this.waveNumber > -1)
            {
                int leftPercentage = 100;
                int rightPercentage = 100;
                try
                {
                    UnitProperties left = Snapshot.UnitProperties[HudApi.GetHudSourceUnit(HudSection.LeftKing)];
                    UnitProperties right = Snapshot.UnitProperties[HudApi.GetHudSourceUnit(HudSection.RightKing)];

                    //leftPercentage = (int)Math.Round(left.Hp.PercentLife); // (int)Math.Round(left.Hp.CurrentLife / left.Hp.MaxLife * 100);
                    //rightPercentage = (int)Math.Round(right.Hp.PercentLife); //Math.Round(right.Hp.CurrentLife / right.Hp.MaxLife * 100);

                    leftPercentage = (int)Math.Round(left.Hp.GetLastLife(Snapshot.RenderTime) / left.Hp.GetMaxLife(Snapshot.RenderTime) * 100);
                    rightPercentage = (int)Math.Round(right.Hp.GetLastLife(Snapshot.RenderTime) / right.Hp.GetMaxLife(Snapshot.RenderTime) * 100);
                    // CurrentLife / MaxLife;

                }
                catch
                {
                    Console.WriteLine("King unavailable, asuming 100%");
                }
                this._queue.Enqueue(
                new QueuedItem(
                    this.configUrl.Value,
                    new WaveStartedPayload(this.waveNumber, this.matchUUID, this.units, this.mercenaries, mythium, leftPercentage, rightPercentage),
                    configStreamDelay.Value
                ));
            }
            return true;
        }

        public void Update()
        {
            //ClientApi.IsSpectator() // returns spectator ## do some logic omg

            if (!this._registered && this._configured && sp.Elapsed.TotalSeconds > 20) // guessing init time of the base class Assets.Features.Hud to prevent accidentally triggering the constructor prematurely;
            {
                this._registered = true;
                Console.WriteLine("Registering event handlers.");

                HudApi.OnPostSetHudTheme += (string theme) =>
                {
                    if (theme == "day" && this._waveSet)
                    {
                        Task.Run(async () => {
                            await Task.Delay(5000);
                            //create gamestate
                            //submit gamestate to server
                            int retries = 0;
                            bool posted = false;
                            do
                            {
                                try
                                {
                                    posted = fetchAndPostWaveStartedInfo(false);
                                    retries++;

                                    if (!posted)
                                    {
                                        await Task.Delay(2000);
                                    }
                                }
                                catch
                                {
                                    retries++;
                                }
                            }
                            while (retries < 5 || !posted);

                            if (!posted)
                            {
                                Console.WriteLine("Failed 4 times, but pushing the data even though it might be lacking some information.");
                                fetchAndPostWaveStartedInfo(true);
                            }
                        });
                        this._waveSet = false;
                    }
                };

                HudApi.OnSetWestEnemiesRemaining += (int i) =>
                {
                    return;
                    if (this._waveSet)
                    {
                        int total = 0;
                        try
                        {
                            total = Assets.States.Components.MercenaryIconHandler.GetMercenaryIconsReceived(1, this.waveNumber).Count;
                            total += Assets.States.Components.MercenaryIconHandler.GetMercenaryIconsReceived(2, this.waveNumber).Count;
                            total += Assets.States.Components.MercenaryIconHandler.GetMercenaryIconsReceived(3, this.waveNumber).Count;
                            total += Assets.States.Components.MercenaryIconHandler.GetMercenaryIconsReceived(4, this.waveNumber).Count;
                        }
                        catch
                        {
                            Console.WriteLine("Issues fetching the mercenaries recceived, skipping for now.");
                        }
                        double treshold = (((WaveInfo.GetWaveInfo(this.waveNumber).AmountSpawned * this.lTDPlayers.FindAll(l => l.player < 5).Count) + total) * 0.7);
                        if (treshold < i)
                        {
                            maxUnitsSeen = i;
                            this._waveSet = true;
                            this._shouldPost = true;
                        }
                    }
                    if (this._shouldPost && (maxUnitsSeen > i || (WaveInfo.GetWaveInfo(this.waveNumber).AmountSpawned * this.lTDPlayers.FindAll(l => l.player < 5).Count) <= i))
                    {
                        // indicates that more than 75% of the wave has spawned, and that the number of creeps is decreasing or that mercs equal or greater than the entire wave has spawned.
                        if (this.retries > 5)
                        {
                            fetchAndPostWaveStartedInfo(true);
                            maxUnitsSeen = 0;
                            this._waveSet = true;
                            this._shouldPost = true;
                            this.retries = 0;
                        }
                        if (fetchAndPostWaveStartedInfo(false))
                        {
                            maxUnitsSeen = 0;
                            this._waveSet = false;
                            this._shouldPost = false;
                            this.retries = 0;
                        }
                        else
                        {
                            this.retries++;
                            // shit failed, reattempt next time it changes ? 
                        }
                        
                    }
                };

                HudApi.OnSetWaveNumber += (i) =>
                {
                    this.waveNumber = i;
                    this._waveSet = true;
                    this.units.Clear();
                    this.mercenaries.Clear();
                    int leftPercentage = 100;
                    int rightPercentage = 100;
                    try
                    {
                        UnitProperties left = Snapshot.UnitProperties[HudApi.GetHudSourceUnit(HudSection.LeftKing)];
                        UnitProperties right = Snapshot.UnitProperties[HudApi.GetHudSourceUnit(HudSection.RightKing)];

                        leftPercentage = (int)Math.Round(left.Hp.GetLastLife(Snapshot.RenderTime) / left.Hp.GetMaxLife(Snapshot.RenderTime) * 100);
                        rightPercentage = (int)Math.Round(right.Hp.GetLastLife(Snapshot.RenderTime) / right.Hp.GetMaxLife(Snapshot.RenderTime) * 100);
                        //leftPercentage = (int)Math.Round(left.Hp.CurrentLife / left.Hp.MaxLife * 100);
                        //rightPercentage = (int)Math.Round(right.Hp.CurrentLife / right.Hp.MaxLife * 100);
                    }
                    catch
                    {
                        Console.WriteLine("King unavailable, asuming 100%");
                    }

                    

                    if (this.waveNumber > 1)
                    {
                        this._queue.Enqueue(
                        new QueuedItem(
                            this.configUrl.Value,
                            new WaveCompletedPayload(this.waveNumber - 1, leftPercentage, rightPercentage, this.leaks, false, this.matchUUID),
                            this.configStreamDelay.Value
                        ));
                    }

                    this.leaks.Clear();
                };

                HudApi.OnRefreshPostGameBuilds += (PostGameBuildsProperties e) =>
                {
                    try
                    {
                        List<PostGameStatsPlayerBuilds> data = new List<PostGameStatsPlayerBuilds>();
                        Dictionary<int, int> indexToPlayer = new Dictionary<int, int>();
                        int n = 0;

                        // pray that the playernames is in order... ? 
                        this.lTDPlayers.OrderBy(o => o.player).ToList().ForEach((p) =>
                        {
                            indexToPlayer.Add(n, p.player);
                            n++;
                        });

                        e.builds.ForEach(b =>
                        {
                            int i = 0;

                            b.playerBuilds.ForEach(c =>
                            {
                                try
                                {
                                    data.Add(new PostGameStatsPlayerBuilds(c, b.number, indexToPlayer[i]));
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine("Unable to add index for player " + i);
                                    Console.WriteLine(ex.Message);
                                }
                                i++;
                            });
                        });

                        int left = (int)Math.Round(e.builds.Last().leftKingPercentHp * 100);
                        int right = (int)Math.Round(e.builds.Last().rightKingPercentHp * 100);
                        int last = e.builds.Last().number;
                        Console.WriteLine("Queueing up a postmatch build summary");
                        this._queue.Enqueue(new QueuedItem(configUrl.Value, new PostGameStatsPayload(data, left, right, last, this.matchUUID), configStreamDelay.Value));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("ISSUES IN THE POSTGAMEBUILDS THINGY!");
                        Console.WriteLine(ex.Message);
                    }
                };

                HudApi.OnRefreshPostGameStats += (PostGameStatsProperties e) =>
                {
                    Console.WriteLine("PostGameStatsRefreshed, asuming the game ended? Sending final payload.");
                    
                    List<MastermindLegion> lmlist = new List<MastermindLegion>();

                    e.leftTeamRows.ForEach(p =>
                    {
                        lmlist.Add(new MastermindLegion(p));
                    });

                    e.rightTeamRows.ForEach(p =>
                    {
                        lmlist.Add(new MastermindLegion(p));
                    });

                    if (e.gameId == this.matchUUID)
                    {
                        this._queue.Enqueue(new QueuedItem(this.configUrl.Value, new GameCompletedPayload(this.waveNumber, this.leaks, true, this.matchUUID, lmlist), configStreamDelay.Value));
                    }
                    else
                    {
                        Console.WriteLine("Found some postmatch info that has unknown origin, please advice.");
                    }                    
                };

                HudApi.OnEnteredGame += (SimpleEvent)delegate
                {
                    Console.WriteLine("Entered new game, fetching players.");

                    this.matchUUID = ClientApi.GetServerLogURL().Split('/').Last(); // should return gameid from ConfigApi.GameId which is internal.

                    this.lTDPlayers.ForEach(p => p.found = false);
                    foreach(ushort player in PlayerApi.GetPlayingPlayers())
                    {
                        int p = this.lTDPlayers.FindIndex(p => p.player == player);
                        LTDPlayer t = p != -1 ? this.lTDPlayers[p] : new LTDPlayer();

                        t.found = true;


                        if (p == -1)
                        {
                            t.image = Snapshot.PlayerProperties[player].Image.Get();
                            GuildEntityObjectProperties guild = Snapshot.PlayerProperties[player].GuildProperties;
                            t.name = Snapshot.PlayerProperties[player].Name;
                            t.countryCode = Snapshot.PlayerProperties[player].CountryCode;
                            t.countryName = Countries.GetCountry(t.countryCode).name;
                            t.guildAvatar = guild.avatar;
                            t.player = player;
                            if (!string.IsNullOrEmpty(t.name) || t.name != "_open" || t.name != "_closed" || t.player < 9 && t.player > 0 || t.name != "(Closed)") {
                                this.lTDPlayers.Add(t);
                            }
                        }
                        else
                        {
                            t.name = Snapshot.PlayerProperties[player].Name;
                            t.countryCode = Snapshot.PlayerProperties[player].CountryCode;
                            t.countryName = Countries.GetCountry(t.countryCode).name;
                            GuildEntityObjectProperties guild = Snapshot.PlayerProperties[player].GuildProperties;
                            t.guildAvatar = guild.avatar;
                            t.guild = guild.guildName;
                        }
                    }
                    this.units.Clear();
                    this.mercenaries.Clear();

                    this.lTDPlayers.RemoveAll(p => p.found != true);

                    int leftPercentage = 100;
                    int rightPercentage = 100;
                    this._queue.Enqueue(new QueuedItem(this.configUrl.Value, new MatchJoinedPayload(this.lTDPlayers, 0, this.matchUUID, leftPercentage, rightPercentage), this.configStreamDelay.Value));

                    // this.postData(new Payload(this.lTDPlayers, this.waveNumber, this.matchUUID)); // intentional non-awaiting async task to prevent hanging up the core thread.
                    
                };

                HudApi.OnRefreshSticker += (props) => {
                    if (string.IsNullOrEmpty(props.name) || props.name == "_open" || props.name == "_closed" || props.player > 8) { return; }
                    int p = this.lTDPlayers.FindIndex(p => p.player == props.player);
                    LTDPlayer t = p != -1? this.lTDPlayers[p] :new LTDPlayer();
                    t.rating = props.rating;
                    t.player = props.player;
                    t.guildAvatar = props.guildAvatar;
                    t.guild = props.guild;
                    t.image = props.image;
                    t.countryName = Countries.GetCountry(t.countryCode).name;

                    if (p == -1)
                    {
                        this.lTDPlayers.Add(t);
                    }
                    
                };

                HudApi.OnDisplayGameText += (string header, string content, float duration, string image) =>
                {
                    if (content.Contains("leak"))
                    {
                        try
                        {
                            this.leaks.Add(new Leaks(
                                int.Parse(content.Split('%')[0].Split('(').Last()),
                                int.Parse(content.Split(')')[0].Split('(')[1])
                                ));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Issues parsing a leak... :seenoevil:");
                            Console.WriteLine(ex.Message);
                        }
                        Console.WriteLine("Leak registered!");
                        Console.WriteLine(content);
                    }
                };

                sp.Stop();
            }
            if (this._queue.Count > 0 && this._queue.First().runAfter < DateTime.Now)
            {
                PostToUrl(this._queue.Dequeue());
            }
        }

        public async Task<bool> PostToUrl(string url, string serializedBody)
        {
            HttpRequestMessage req = new HttpRequestMessage();
            req.Method = HttpMethod.Post;
            req.Content = new StringContent(serializedBody, Encoding.UTF8, "application/json");
            req.RequestUri = new Uri(url);
            HttpResponseMessage rm = await _client.SendAsync(req);
            try
            {
                rm.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                return false;
            }
        }

        public async Task<bool> PostToUrl(QueuedItem queuedItem)
        {
            HttpRequestMessage req = new HttpRequestMessage();
            req.Method = HttpMethod.Post;
            req.Content = new StringContent(queuedItem.serializedBody, Encoding.UTF8, "application/json");
            req.RequestUri = new Uri(queuedItem.url);
            HttpResponseMessage rm = await _client.SendAsync(req);
            try
            {
                rm.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                return false;
            }
        }

        public class QueuedItem
        {
            public string url;
            public string serializedBody;
            public DateTime runAfter;

            public QueuedItem(string url, string serializedBody, int streamDelay)
            {
                this.url = url;
                this.serializedBody = serializedBody;
                this.runAfter = (DateTime.Now).AddSeconds(streamDelay);
            }

            public QueuedItem(string url, object body, int streamDelay)
            {
                this.url = url;
                this.serializedBody = JsonConvert.SerializeObject(body);
                this.runAfter = (DateTime.Now).AddSeconds(streamDelay);
                
            }
        }

        public class MatchJoinedPayload
        {
            public List<LTDPlayer> players;
            public int wave;
            public string matchUUID;
            public int leftKingHP;
            public int rightKingHP;

            public MatchJoinedPayload(List<LTDPlayer> players, int wave, string matchUUID, int leftKingHP, int rightKingHP)
            {
                this.players = players;
                this.wave = wave;
                this.matchUUID = matchUUID;
                this.leftKingHP = leftKingHP;
                this.rightKingHP = rightKingHP;
            }
        }

        public class MythiumRecceived
        {
            public int mythium;
            public int player;

            public MythiumRecceived(int mythium, int player)
            {
                this.mythium = mythium;
                this.player = player;
            }
        }

        public class PostGameStatsPayload
        {
            public List<PostGameStatsPlayerBuilds> stats;
            public int leftKingHP;
            public int rightKingHP;
            public int wave; // the wave it actually ended on.
            public string matchUUID;
            public PostGameStatsPayload(List<PostGameStatsPlayerBuilds> stats, int leftKingHP, int rightKingHP, int wave ,string matchUUID)
            {
                this.stats = stats;
                this.leftKingHP = leftKingHP;
                this.rightKingHP = rightKingHP;
                this.wave = wave;
                this.matchUUID = matchUUID;
            }
        }

        public class PostGameStatsPlayerBuilds
        {
            public int wave;
            public int fighterValue;
            public int workers;
            public int recommendedValue;
            public List<string> unitsLeaked;
            public int player;
            public PostGameStatsPlayerBuilds(PostGamePlayerBuildFighterProperties p, int waveNumber, int player)
            {
                this.wave = waveNumber;
                this.fighterValue = p.fightersValue;
                this.workers = p.workers;
                this.recommendedValue = p.recommendedValue;
                this.unitsLeaked = p.unitsLeaked;
                this.player = player;
            }
        }

        public class LTDPlayer
        {
            #nullable enable
            public int player { get; set; }
            public string? name { get; set; }
            public string? guild { get; set; }
            public int rating { get; set; }
            public string? image { get; set; }
            public int avatarStacks { get; set; }
            public string? countryCode { get; set; }
            public int lastSeasonRating { get; set; }
            public string? countryName { get; set; }
            public string? guildAvatar { get; set; }
            public string? guildAvatarStacks { get; set; }

            [JsonIgnore]
            public bool found = true;
        }

        public class Recceived
        {
            public string image { get; set; }
            public int player { get; set; }
            
            public int count { get;set; }
            public Recceived (string image, int player, int count)
            {
                this.image = image;
                this.player = player;
                this.count = count;
            }
        }

        public class WaveStartedPayload
        {
            public int wave;
            public string matchUUID;
            public List<Unit> units;
            public List<Recceived> recceived;
            public List<MythiumRecceived> recceivedAmount;
            public int leftKingWaveStartHP;
            public int rightKingWaveStartHP;


            public WaveStartedPayload(int wave, string matchUUID, List<Unit>units, List<Recceived>received, List<MythiumRecceived> recceivedAmount, int leftPercentage, int rightPercentage)
            {
                this.units = units;
                this.recceived = received;
                this.wave = wave;
                this.matchUUID = matchUUID;
                this.recceivedAmount = recceivedAmount;
                this.leftKingWaveStartHP = leftPercentage;
                this.rightKingWaveStartHP = rightPercentage;
            }
        }

        public class WaveCompletedPayload
        {
            public string matchUUID; 
            public int wave;
            public int leftKingHP;
            public int rightKingHP;
            public List<Leaks> leaks;
            public bool lastWave;

            public WaveCompletedPayload(int wave, int leftKingHP, int rightKingHP, List<Leaks> leaks, bool lastWave, string matchUUID)
            {
                this.wave = wave;
                this.leftKingHP = leftKingHP;
                this.rightKingHP = rightKingHP;
                this.leaks = leaks;
                this.lastWave = lastWave;
                this.matchUUID = matchUUID;
            }
        }

        public class GameCompletedPayload
        {
            public string matchUUID;
            public int wave;
            public List<Leaks> leaks;
            public bool lastWave;
            public List<MastermindLegion> legionMastermind;

            public GameCompletedPayload(int wave, List<Leaks> leaks, bool lastWave, string matchUUID, List<MastermindLegion> legionMastermind)
            {
                this.wave = wave;
                this.leaks = leaks;
                this.lastWave = lastWave;
                this.matchUUID = matchUUID;
                this.legionMastermind = legionMastermind;
            }
        }

        public class MastermindLegion
        {
            public int player;
            public string playstyle;
            public string playstyleIcon;
            public string spell;
            public string spellIcon;
            public int mvpScore;
            public MastermindLegion(PostGameStatsRowProperties p)
            {
                this.player = p.number;
                this.playstyle = p.playstyle;
                this.playstyleIcon = p.playstyleIcon;
                this.spell = p.spell;
                this.spellIcon = p.spellIcon;
                this.mvpScore = p.mvpScore;
            }
        }

        public class Leaks
        {
            public int percentage;
            public int player;

            public Leaks(int percentage, int player)
            {
                this.percentage = percentage;
                this.player = player;
            }
        }

        public class Unit
        {
            public int x;
            public int y;
            [JsonProperty("displayName")]
            public string name;
            [JsonProperty("name")]
            public string image;
            public int player;

            public Unit(int x, int y, string name, string image, int recceivingPlayer)
            {
                this.x = x;
                this.y = y;
                this.name = name;
                this.image = image;
                this.player = recceivingPlayer;
            }
        }

        public void OnDestroy() {
            UnPatch();
        }

        private void Patch() {
            _harmony.PatchAll(_assembly);
        }

        private void UnPatch() {
            _harmony.UnpatchSelf();
        }

    }
}