using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Assets.Features.Hud;
using BepInEx;
using HarmonyLib;
using aag.Natives;
using aag.Natives.Lib.Primitives;
using Assets.Api;
using aag.Natives.Api;
using aag.Natives.Lib.ViewObjects;
using aag.Natives.Lib.Guilds;
using aag.Natives.Lib.Misc;
using Newtonsoft.Json;
using System.Net.Http;
using System.Text;
using BepInEx.Configuration;
using aag.Natives.Lib.Properties;
using System.Timers;

namespace Logless
{
    using static Logless.Plugin;
    using P = Plugin;

    [BepInProcess("Legion TD 2.exe")]
    [BepInPlugin("UnitTracker", "UnitTracker", "1.4.1")]
    public class Plugin : BaseUnityPlugin
    {
        private readonly Assembly _assembly = Assembly.GetExecutingAssembly();
        private readonly Harmony _harmony = new("UnitTracker");
        private Timer timer = new Timer();
        private Timer eventTimer = new Timer();
        private bool _registered = false;
        private int actualWaveNumber = 0;
        private int ingameWaveNumber = 0;
        private List<LTDPlayer> lTDPlayers = new List<LTDPlayer>();
        private List<Unit> units = new List<Unit>();
        private List<Recceived> mercenaries = new List<Recceived>();
        private HttpClient _client = new HttpClient();
        private bool _configured = false;
        private bool _waveSet = false;
        private ConfigEntry<string> configUrl;
        private ConfigEntry<string> configJwt;
        private ConfigEntry<int> configStreamDelay;
        private ConfigEntry<bool> configShowTTL;
        private int maxUnitsSeen = 0;
        private bool _shouldPost = false;
        private string matchUUID = "";
        private Queue<QueuedItem> _queue = new Queue<QueuedItem>();
        private List<Leaks> leaks = new List<Leaks>();
        private int retries = 0;
        private bool waveStartSynced = false;
        private bool firstWaveSet = false;
        private bool sentMastermind = false;
        private bool sentSpells = false;
        private Stopwatch waveElapsedStopWatch = new Stopwatch();

        public void Awake()
        {
            configUrl = Config.Bind("General", "UpdateURL", "https://ltd2.krettur.no/v2/update", "HTTPS endpoint to post on waveStarted event, default is the extension used by the Twitch Overlay.");
            configJwt = Config.Bind("General", "JWT", "", "JWT to authenticate your data, reach out to @bosen in discord to get your token for the Twitch Overlay.");
            configStreamDelay = Config.Bind("General", "StreamDelay", 0, "Delay before data is pushed, in seconds. Set this equal to the delay configued in OBS / Stream Labs to prevent the overlay showing information from the future");
            configShowTTL = Config.Bind("General", "ShowTTL", true, "Whether or not to show the time it took a player to leak in game via the HUD");

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
            this.waveElapsedStopWatch.Start();

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

            this.timer.Interval = 20000;

            this.timer.Elapsed += init;
            this.timer.Enabled = true;


            this.eventTimer.Interval = 1000;
            this.eventTimer.AutoReset = true;
            this.eventTimer.Elapsed += processEvents;
            this.eventTimer.Enabled = true;
        }
#nullable enable
        private QueuedItem? fetchWaveStartedInfo(List<MastermindLegion>? legionMastermind = null)
        {
            List<PostGameStatsPlayerBuilds> pgs = new List<PostGameStatsPlayerBuilds>();
            List<MythiumRecceived> mythium = new List<MythiumRecceived>();
            try
            {
                this.mercenaries.Clear();
                foreach (ushort p in PlayerApi.GetPlayingPlayers())
                {
                    Log("Fetching for player " + p);
                    LTDPlayer player = this.lTDPlayers.Find(pl => pl.player == p);

                    Dictionary<IntVector2, Scoreboard.ScoreboardGridData> data = Scoreboard.GetGridData(p);


                    //commented out, if a player sold all fighters state should still be updated
                    //if(data.Count < 1)
                    //{
                    //    throw new Exception("Nope, no units for this bad boi");
                    //}

                    Log("Found " + data.Count + "Entries... adding them to the list");


                    foreach (IntVector2 key in data.Keys)
                    {
                        int index = this.units.FindIndex(u => u.player == p && u.x == key.x && u.y == key.y);
                        if (index == -1)
                        {
                            this.units.Add(new Unit(key.x, key.y, data[key].UnitType, data[key].Image, player.player));
                        }
                    }

                    List<string> mercs = Assets.States.Components.MercenaryIconHandler.GetMercenaryIconsReceived(p, ingameWaveNumber);

                    //mercenaries need to be cleared between queries, else their count will increase indefinitely


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

                    mythium.Add(new MythiumRecceived(Snapshot.PlayerProperties[p].MythiumReceivedPerWave[this.ingameWaveNumber], p));
                }
            }
            catch (Exception e)
            {
                Log("wave query generated an exception: " + e.ToString());

                return null;
            }


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
            Log("wave query was successfull.");
            WaveStartedPayload wsp = new WaveStartedPayload(this.actualWaveNumber, this.matchUUID, this.units, this.mercenaries, mythium, leftPercentage, rightPercentage);
            this.lTDPlayers.ForEach(p =>
            {
                List<string> rolls;
                PlayerProperties pp = Snapshot.PlayerProperties[p.player];
                if (ClientApi.IsSpectator())
                {
                    rolls = (from roll in pp.Rolls.Get()
                             select MapApi.Get("units", roll, "iconpath").Value).ToList();
                }
                else
                {
                    rolls = new List<string>();
                }
                pgs.Add(new PostGameStatsPlayerBuilds(this.actualWaveNumber, pp.GetTowerValue(), (ClientApi.IsSpectator() ? pp.GetWorkerCount() : null), pp.GetRecommendedValue(this.ingameWaveNumber), p.player, rolls));
            });
            if (pgs != null)
            {
                wsp = wsp.AddPostGameStatsPlayerbuilds(pgs);
            }
            if (legionMastermind != null)
            {
                wsp = wsp.AddLegionMastermind(legionMastermind);
            }
            return
                new QueuedItem(
                    this.configUrl.Value,
                    wsp,
                    configStreamDelay.Value
                );

        }

#nullable disable
        void Log(string msg)
        {
            Logger.LogInfo("UNITTRACKER " + System.DateTime.UtcNow.ToString("yyyy-MM-dd--HH-mm-ss-ffff") + " " + msg);
        }
        public void init(object source, System.Timers.ElapsedEventArgs eArgs)
        {
            //ClientApi.IsSpectator() // returns spectator ## do some logic omg

            this._registered = true;
            Log("Registering event handlers.");

            HudApi.OnPostSetHudTheme += theme =>
            {
                Log("OnPostSetHudTheme: " + theme);
                if (theme == "day")
                {
                    this.waveElapsedStopWatch.Restart();
                    if (this.waveStartSynced && !ClientApi.IsSpectator()) return;
                    this.waveStartSynced = true;

                    if (this.ingameWaveNumber < 21)
                    {
                        this.actualWaveNumber = this.ingameWaveNumber;
                    }
                    else
                    {
                        if (!this.firstWaveSet)
                        {
                            this.actualWaveNumber = WaveInfo.CurrentWaveNumber;
                            this.firstWaveSet = true;
                        }
                        else
                        {
                            this.actualWaveNumber += 1;
                        }

                        if (this.actualWaveNumber <= 0)
                        {
                            this.actualWaveNumber = 1;
                        }
                    }
#nullable enable
                    List<MastermindLegion>? legionMastermind = null;

                    // send the masterminds on the first available wave
                    if (ClientApi.IsSpectator())
                    {
                        if (!this.sentMastermind)
                        {
                            legionMastermind = new List<MastermindLegion>();

                            this.lTDPlayers.ForEach(p =>
                            {
                                PlayerProperties pp = Snapshot.PlayerProperties[p.player];
                                legionMastermind.Add(new MastermindLegion(p.player).addPlaystyle("unknown", pp.Image));
                            });

                            this.sentMastermind = true;
                        }


                        // send kingspells if its wave 11 or later
                        if (!this.sentSpells && this.actualWaveNumber >= 11)
                        {
                            if (legionMastermind == null)
                            {
                                legionMastermind = new List<MastermindLegion>();
                            }
                            this.lTDPlayers.ForEach(p =>
                            {
                                int i = legionMastermind.FindIndex(l => l.player == p.player);
                                MastermindLegion t = i != -1 ? legionMastermind[i] : new MastermindLegion(p.player);
                                PlayerProperties pp = Snapshot.PlayerProperties[p.player];
                                t.addSpell(pp.PowerupSelected, MapApi.Get("powerups", pp.PowerupSelected, "iconpath"));

                                if (i == -1)
                                {
                                    legionMastermind.Add(t);
                                }
                            });
                            this.sentSpells = true;
                        }
                    }

#nullable disable

                    Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        var prevState = fetchWaveStartedInfo(legionMastermind);
                        var lastState = prevState;
                        int retries = 0;
                        bool done = false;
                        while (!done && retries < 6)
                        {
                            await Task.Delay(1000);
                            var newState = fetchWaveStartedInfo(legionMastermind);
                            if (prevState == null || prevState?.serializedBody != newState?.serializedBody)
                            {
                                Log("wave state has changed OR wave query was unsuccessful.");
                                Log("old state: " + prevState?.serializedBody);
                                Log("new state: " + newState?.serializedBody);
                                prevState = newState;
                            }
                            else
                            {
                                Log("submitting wave state to server: " + newState?.serializedBody);
                                this._queue.Enqueue(newState);
                                done = true;
                            }

                            if (retries == 6)
                            {
                                lastState = newState;
                            }
                        }
                        if (!done)
                        {
                            Log("Submitting an unfinished state " + lastState?.serializedBody);
                            this._queue.Enqueue(lastState);
                        }
                    });
                }
                else
                {
                    this.waveStartSynced = false;
                }
            };

            HudApi.OnSetWaveNumber += (i) =>
            {
                this.ingameWaveNumber = i;
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
                }
                catch
                {
                    Console.WriteLine("King unavailable, asuming 100%");
                }

                if (!this.firstWaveSet)
                {
                    this.actualWaveNumber = i;
                    this.firstWaveSet = true;
                }
                else
                {

                    if (this.actualWaveNumber > 1)
                    {
                        var payload =
                        new QueuedItem(
                            this.configUrl.Value,
                            new WaveCompletedPayload(this.actualWaveNumber, leftPercentage, rightPercentage, this.leaks, false, this.matchUUID),
                            this.configStreamDelay.Value
                        );
                        Log("enqueuing waveCompletedPayload: " + payload.serializedBody);
                        this._queue.Enqueue(payload);
                    }
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
                    Log("e.Builds.Count: " + e.builds.Count);

                    //reformatted to send actual wave number in payload, rather than ingame wave number
                    for (int i_builds = 0; i_builds < e.builds.Count; i_builds++)
                    {
                        for (int i_playerBuilds = 0; i_playerBuilds < e.builds[i_builds].playerBuilds.Count; i_playerBuilds++)
                        {
                            try
                            {
                                data.Add(new PostGameStatsPlayerBuilds(e.builds[i_builds].playerBuilds[i_playerBuilds], i_builds + 1, indexToPlayer[i_playerBuilds]));
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("Unable to add index for player " + i_playerBuilds);
                                Console.WriteLine(ex.Message);
                            }
                        }
                    }

                    int left = (int)Math.Round(e.builds.Last().leftKingPercentHp * 100);
                    int right = (int)Math.Round(e.builds.Last().rightKingPercentHp * 100);
                    int last = e.builds.Last().number;

                    var payload = new QueuedItem(configUrl.Value, new PostGameStatsPayload(data, left, right, last, this.matchUUID), configStreamDelay.Value);
                    Log("Queueing up a postmatch build summary: " + payload.serializedBody);
                    this._queue.Enqueue(payload);
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
                    var payload = new QueuedItem(this.configUrl.Value, new GameCompletedPayload(this.actualWaveNumber, this.leaks, true, this.matchUUID, lmlist), configStreamDelay.Value);
                    Log("Enqueuing final payload: " + payload.serializedBody);
                    this._queue.Enqueue(payload);
                }
                else
                {
                    Console.WriteLine("Found some postmatch info that has unknown origin, please advice.");
                }
            };

            HudApi.OnEnteredGame += (SimpleEvent)delegate
            {
                Console.WriteLine("Entered new game, fetching players.");
                this.sentMastermind = false;
                this.sentSpells = false;

                this.actualWaveNumber = 0;
                this.firstWaveSet = false;

                this.matchUUID = ClientApi.GetServerLogURL().Split('/').Last(); // should return gameid from ConfigApi.GameId which is internal.

                this.lTDPlayers.ForEach(p => p.found = false);
                foreach (ushort player in PlayerApi.GetPlayingPlayers())
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
                        if (!string.IsNullOrEmpty(t.name) || t.name != "_open" || t.name != "_closed" || t.player < 9 && t.player > 0 || t.name != "(Closed)")
                        {
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

                List<string> source = Snapshot.PlayerProperties[ClientApi.GetLocalPlayer()].PowerupChoices.Get().ToList();

                List<string> powerupChoices = source.Select((string powerup) => MapApi.Get("powerups", powerup, "iconpath").Value).ToList();

                var payload = new QueuedItem(this.configUrl.Value, new MatchJoinedPayload(this.lTDPlayers, 0, this.matchUUID, leftPercentage, rightPercentage, powerupChoices), this.configStreamDelay.Value);
                Log("Enqueuing new game payload: " + payload.serializedBody);
                this._queue.Enqueue(payload);


                // this.postData(new Payload(this.lTDPlayers, this.waveNumber, this.matchUUID)); // intentional non-awaiting async task to prevent hanging up the core thread.

            };

            HudApi.OnRefreshSticker += (props) =>
            {
                if (string.IsNullOrEmpty(props.name) || props.name == "_open" || props.name == "_closed" || props.player > 8) { return; }
                int p = this.lTDPlayers.FindIndex(p => p.player == props.player);
                LTDPlayer t = p != -1 ? this.lTDPlayers[p] : new LTDPlayer();
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
                if (content.Contains("cleared"))
                {
                    //Log("header " + header + " content: " + content);
                }
                if (content.Contains("player_cleared_lane"))
                {
                    // {"apexId":"player_cleared_lane","customStringDynamicOverrides":["|player(1)"]}
                    try
                    {
                        if (configShowTTL.Value == true)
                        {
                            HudApi.DisplayGameText("This is a header", @$"{content.Split('[')[1].Split(']')[0].Replace("\"", "")} cleared in {Math.Round(this.waveElapsedStopWatch.Elapsed.TotalSeconds, 1)} seconds.", 10f, image);
                        }

                    }
                    catch (Exception ex)
                    {
                        Log("Issues parsing a clear :O");
                        Log(ex.Message);
                    }
                }
                if (content.Contains("leak"))
                {
                    // |player(1) leaked |c(ff8800):(50%)|r
                    try
                    {
                        int player = int.Parse(content.Split(')')[0].Split('(')[1]);
                        this.leaks.Add(new Leaks(
                        int.Parse(content.Split('%')[0].Split('(').Last()),
                            player)
                        );
                        if (configShowTTL.Value == true)
                        {
                            HudApi.DisplayGameText("This is a header", @$"|player({player}) leaked in {Math.Round(this.waveElapsedStopWatch.Elapsed.TotalSeconds, 1)} seconds.", 10f, image);
                        }
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

            this.timer.Enabled = false;
        }

        public void processEvents(object sender, ElapsedEventArgs e)
        {
            if (this._queue.Count > 0 && this._queue.First().runAfter < DateTime.Now)
            {
                PostToUrl(this._queue.Dequeue()).ConfigureAwait(false);
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
            public List<string> powerupChoices;

            public MatchJoinedPayload(List<LTDPlayer> players, int wave, string matchUUID, int leftKingHP, int rightKingHP, List<string> powerupChoices)
            {
                this.players = players;
                this.wave = wave;
                this.matchUUID = matchUUID;
                this.leftKingHP = leftKingHP;
                this.rightKingHP = rightKingHP;
                this.powerupChoices = powerupChoices;
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

            public PostGameStatsPayload(List<PostGameStatsPlayerBuilds> stats, int leftKingHP, int rightKingHP, int wave, string matchUUID)
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

            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public float workers;
            public int recommendedValue;
            public List<string> unitsLeaked;
            public int player;
            public List<string> rolls;
            public PostGameStatsPlayerBuilds(PostGamePlayerBuildFighterProperties p, int waveNumber, int player)
            {
                this.wave = waveNumber;
                this.fighterValue = p.fightersValue;
                this.workers = (float)Math.Round((decimal)p.workers, 2);
                this.recommendedValue = p.recommendedValue;
                this.unitsLeaked = p.unitsLeaked;
                this.player = player;
                this.rolls = p.rolls;
            }

            public PostGameStatsPlayerBuilds(int wave, int figherValue, float? workers, int recommendedValue, int player, List<string> rolls)
            {
                this.wave = wave;
                this.fighterValue = figherValue;
                if (workers != null)
                {
                    this.workers = (float)Math.Round((decimal)workers, 2);
                }
                this.recommendedValue = recommendedValue;
                this.player = player;
                this.rolls = rolls;
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

            public int count { get; set; }
            public Recceived(string image, int player, int count)
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
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<PostGameStatsPlayerBuilds>? stats;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<MastermindLegion>? legionMastermind;

            public WaveStartedPayload(int wave, string matchUUID, List<Unit> units, List<Recceived> received, List<MythiumRecceived> recceivedAmount, int leftPercentage, int rightPercentage)
            {
                this.units = units;
                this.recceived = received;
                this.wave = wave;
                this.matchUUID = matchUUID;
                this.recceivedAmount = recceivedAmount;
                this.leftKingWaveStartHP = leftPercentage;
                this.rightKingWaveStartHP = rightPercentage;
            }

            public WaveStartedPayload AddPostGameStatsPlayerbuilds(List<PostGameStatsPlayerBuilds> pgs)
            {
                this.stats = pgs;
                return this;
            }

            public WaveStartedPayload AddLegionMastermind(List<MastermindLegion> legionMastermind)
            {
                this.legionMastermind = legionMastermind;
                return this;
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
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string? playstyle;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string? playstyleIcon;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string? spell;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string? spellIcon;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public int? mvpScore;
            public MastermindLegion(PostGameStatsRowProperties p)
            {
                this.player = p.number;
                this.playstyle = p.playstyle;
                this.playstyleIcon = p.playstyleIcon;
                this.spell = p.spell;
                this.spellIcon = p.spellIcon;
                this.mvpScore = p.mvpScore;
            }

            public MastermindLegion(int player, string playstyle, string playstyleIcon, string spell, string spellIcon, int mvpScore)
            {
                this.player = player;
                this.playstyle = playstyle;
                this.playstyleIcon = playstyleIcon;
                this.spell = spell;
                this.spellIcon = spellIcon;
                this.mvpScore = mvpScore;
            }

            public MastermindLegion(int player)
            {
                this.player = player;
            }

            public MastermindLegion addPlaystyle(string playstyle, string playstyleIcon)
            {
                this.playstyle = playstyle;
                this.playstyleIcon = playstyleIcon;
                return this;
            }

            public MastermindLegion addSpell(string spell, string spellIcon)
            {
                this.spell = spell;
                this.spellIcon = spellIcon;
                return this;
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

        public void OnDestroy()
        {
            UnPatch();
        }

        private void Patch()
        {
            _harmony.PatchAll(_assembly);
        }

        private void UnPatch()
        {
            _harmony.UnpatchSelf();
        }

    }
}
