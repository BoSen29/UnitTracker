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

namespace Logless
{
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
        private HttpClient _client = new HttpClient();
        private bool _configured = false;
        private bool _waveSet = false;
        private ConfigEntry<string> configUrl;
        private ConfigEntry<string> configJwt;
        private ConfigEntry<int> configStreamDelay;
        private int maxUnitsSeen = 0;
        private bool _shouldPost = false;
        public void Awake() {
            configUrl = Config.Bind("General", "URL", "https://ltd2.krettur.no/v2/jwtbadboi", "HTTPS endpoint to post the gamestate, default is the extension used by the Twitch Overlay.");
            configJwt = Config.Bind("General", "JWT", "", "JWT to authenticate your data, reach out to @bosen in discord to get your token for the Twitch Overlay.");
            configStreamDelay = Config.Bind("General", "StreamDelay", 0, "Delay before data is pushed, in seconds. Set this equal to the delay configued in OBS / Stream Labs to prevent the overlay showing information from the future");

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

        private void fetchAndPost()
        {
            foreach (ushort p in PlayerApi.GetPlayingPlayers())
            {
                Console.WriteLine("Fetching for player " + p);
                LTDPlayer player = this.lTDPlayers.Find(pl => pl.player == p);

                Dictionary<IntVector2, Scoreboard.ScoreboardGridData> data = Scoreboard.GetGridData(p);

                Console.WriteLine("Found " + data.Count + "Entries... adding them to the list");
                foreach (IntVector2 key in data.Keys)
                {
                    player.units.Add(new Unit(key.x, key.y, data[key].UnitType, data[key].Image));
                }

                List<string> mercs = Assets.States.Components.MercenaryIconHandler.GetMercenaryIconsReceived(p, this.waveNumber);

                foreach (string merc in mercs)
                {
                    player.mercenaries.Add(new Recceived(merc));
                }
            }
            postData(new Payload(this.lTDPlayers, this.waveNumber)); // intentional not awating to prevent locking the main thread.
        }

        public void Update()
        {
            if (!this._registered && this._configured && sp.Elapsed.TotalSeconds > 20) // guessing init time of the base class Assets.Features.Hud to prevent accidentally triggering the constructor prematurely;
            {
                this._registered = true;
                Console.WriteLine("Registering event handlers.");

                HudApi.OnSetWestEnemiesRemaining += (int i) =>
                {
                    //Console.WriteLine(WaveInfo.GetWaveInfo(this.waveNumber).AmountSpawned * this.lTDPlayers.FindAll(l => l.player < 5).Count + " and the count is " + i);
                    if (this._waveSet)
                    {
                        double treshold = (WaveInfo.GetWaveInfo(this.waveNumber).AmountSpawned * this.lTDPlayers.FindAll(l => l.player < 5).Count / 1.7);
                        if (treshold < i)
                        {
                            maxUnitsSeen = i;
                            this._waveSet = true;
                            this._shouldPost = true;
                        }
                    }

                    if (this._shouldPost && maxUnitsSeen > i || (WaveInfo.GetWaveInfo(this.waveNumber).AmountSpawned * this.lTDPlayers.FindAll(l => l.player < 5).Count) <= i)
                    {
                        // indicates that more than 75% of the wave has spawned, and that the number of creeps is decreasing or that mercs equal or greater than the entire wave has spawned.
                        fetchAndPost();
                        maxUnitsSeen = 0;
                        this._waveSet = false;
                        this._shouldPost = false;
                    }
                };
                EnvironmentApi.OnEnableGridOverlay += (bool b) =>
                {
                    // subscribe to gridoverlay incase lockwave=true
                    if (b)
                    {
                        this._waveSet = true;
                        this.lTDPlayers.ForEach(f =>
                        {
                            f.units.Clear();
                            f.mercenaries.Clear();
                        });
                    }
                };



                HudApi.OnSetWaveNumber += (i) =>
                {
                    this.waveNumber = i;
                    this._waveSet = true;

                    this.lTDPlayers.ForEach(f => {
                        f.units.Clear();
                        f.mercenaries.Clear();
                    });
                };

                HudApi.OnEnteredGame += (SimpleEvent)delegate
                {
                    Console.WriteLine("Entered new game, fetching players.");

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
                            // clear incase there is remains from the previous game, as we're not redoing the entire thing here....
                            t.units.Clear();
                            t.mercenaries.Clear();
                        }
                    }

                    this.lTDPlayers.RemoveAll(p => p.found != true);
                    this.postData(new Payload(this.lTDPlayers, this.waveNumber)); // intentional non-awaiting async task to prevent hanging up the core thread.
                    
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

                sp.Stop();
            }
        }

        public async Task<bool> postData(Payload data)
        {
            Console.WriteLine("Posting update to the server in " + configStreamDelay.Value + " seconds.");
            string payload = Newtonsoft.Json.JsonConvert.SerializeObject(data);
            await Task.Delay(configStreamDelay.Value * 1000);

            HttpRequestMessage req = new HttpRequestMessage();
            req.Method = HttpMethod.Post;
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            req.RequestUri = new Uri(configUrl.Value);
            HttpResponseMessage rm = await _client.SendAsync(req);
            // this isn't awaited, so actually no need to return anything
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

        public class LTDPlayer
        {
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

            public List<Unit> units { get; set; } = new List<Unit>();
            public List<Recceived> mercenaries { get; set; } = new List<Recceived>();
        }

        public class Recceived
        {
            public string image { get; set; }

            public Recceived (string image)
            {
                this.image = image;
            }
        }

        public class Payload
        {
            public List<LTDPlayer> players;
            public int wave;

            public Payload(List<LTDPlayer> players, int wave)
            {
                this.players = players;
                this.wave = wave;
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

            public Unit(int x, int y, string name, string image)
            {
                this.x = x;
                this.y = y;
                this.name = name;
                this.image = image;
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