using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Serilog;

namespace EliteDangerousPlugin;

public sealed class EliteJournalWatcher : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _journalDirectory;
    private readonly object _readLock = new();
    private readonly object _statusLock = new();

    private FileSystemWatcher? _journalWatcher;
    private FileSystemWatcher? _statusWatcher;
    private Timer? _pollTimer;
    private Timer? _folderRetryTimer;

    private readonly ConcurrentDictionary<string, long> _journalOffsets = new();
    private string _currentJournalFile = "";
    private bool _initialized;
    private bool _silentReplay = true;
    private bool _disposed;

    public EliteStatusModel Status { get; } = new();

    // ─── Eventi di stato generale ───────────────────────────────
    public event EventHandler<string>? SystemChanged;
    public event EventHandler<string>? StationChanged;
    public event EventHandler<(string Commander, string Ship)>? CommanderChanged;
    public event EventHandler<(string Ship, string Name, string Ident)>? ShipChanged;
    public event EventHandler<(double X, double Y, double Z)>? StarPosChanged;

    // ─── Eventi da Status.json ──────────────────────────────────
    public event EventHandler<bool>? DockedChanged;
    public event EventHandler<bool>? LandedChanged;
    public event EventHandler<bool>? LandingGearChanged;
    public event EventHandler<bool>? ShieldsChanged;
    public event EventHandler<bool>? SupercruiseChanged;
    public event EventHandler<bool>? FlightAssistChanged;
    public event EventHandler<bool>? HardpointsChanged;
    public event EventHandler<bool>? FsdChargingChanged;
    public event EventHandler<bool>? FsdCooldownChanged;
    public event EventHandler<bool>? LowFuelChanged;
    public event EventHandler<bool>? InWingChanged;
    public event EventHandler<bool>? LightsOnChanged;
    public event EventHandler<bool>? CargoScoopChanged;
    public event EventHandler<bool>? SilentRunningChanged;
    public event EventHandler<bool>? ScoopingFuelChanged;
    public event EventHandler<bool>? FsdMassLockedChanged;
    public event EventHandler<bool>? OverheatingChanged;
    public event EventHandler<bool>? InDangerChanged;
    public event EventHandler<bool>? BeingInterdictedChanged;
    public event EventHandler<bool>? InMainShipChanged;
    public event EventHandler<bool>? InFighterChanged;
    public event EventHandler<bool>? InSrvChanged;

    // ─── Valori da Status.json ──────────────────────────────────
    public event EventHandler<string>? LegalStateChanged;
    public event EventHandler<string>? BodyNameChanged;
    public event EventHandler<(double Main, double Reservoir, double Capacity)>? FuelChanged;
    public event EventHandler<(double Tons, double Capacity)>? CargoChanged;
    public event EventHandler<long>? BalanceChanged;
    public event EventHandler<(int Sys, int Eng, int Wep)>? PipsChanged;
    public event EventHandler<(double Lat, double Lon, double Alt, int Head)>? PositionChanged;

    // ─── Gradi e progressi ──────────────────────────────────────
    public event EventHandler<(int Combat, int Trade, int Explore, int Empire, int Federation, int Cqc)>? RankChanged;
    public event EventHandler<(int Combat, int Trade, int Explore, int Empire, int Federation, int Cqc)>? ProgressChanged;

    // ─── Combattimento ──────────────────────────────────────────
    public event EventHandler<bool>? UnderAttackChanged;
    public event EventHandler<bool>? InterdictedChanged;
    public event EventHandler<bool>? EscapedInterdictionChanged;
    public event EventHandler<long>? BountyChanged;
    public event EventHandler<double>? HullDamageChanged;

    // ─── Missioni ───────────────────────────────────────────────
    public event EventHandler<int>? MissionCountChanged;
    public event EventHandler<string>? MissionNameChanged;
    public event EventHandler<long>? MissionRewardChanged;

    // ─── Esplorazione ───────────────────────────────────────────
    public event EventHandler<string>? ScanBodyChanged;
    public event EventHandler<string>? ScanTypeChanged;
    public event EventHandler<string>? MaterialChanged;
    public event EventHandler<int>? FssBodiesChanged;

    // ─── Mercato ────────────────────────────────────────────────
    public event EventHandler<long>? MarketProfitChanged;

    // ─── Flags2 Odyssey ─────────────────────────────────────────
    public event EventHandler<bool>? OnFootChanged;
    public event EventHandler<bool>? InTaxiChanged;
    public event EventHandler<bool>? InMulticrewChanged;
    public event EventHandler<bool>? LowOxygenChanged;
    public event EventHandler<bool>? LowHealthChanged;

    public EliteJournalWatcher(ILogger logger)
    {
        _logger = logger.ForContext<EliteJournalWatcher>();
        _journalDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Saved Games", "Frontier Developments", "Elite Dangerous");

        if (!Directory.Exists(_journalDirectory))
        {
            _logger.Warning("Journal directory not found: {Dir} - will retry", _journalDirectory);
            _folderRetryTimer = new Timer(_ => TryInit(), null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            return;
        }

        InitWatchers();
    }

    private void TryInit()
    {
        if (_disposed) return;
        if (!Directory.Exists(_journalDirectory)) return;

        _logger.Information("Journal directory found, initializing");
        _folderRetryTimer?.Dispose();
        _folderRetryTimer = null;
        InitWatchers();
        Start();
    }

    private void InitWatchers()
    {
        _logger.Information("Journal directory: {Dir}", _journalDirectory);

        _statusWatcher = new FileSystemWatcher(_journalDirectory, "Status.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _statusWatcher.Changed += (_, _) => ReadStatusFile();

        _journalWatcher = new FileSystemWatcher(_journalDirectory, "Journal.*.log")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _journalWatcher.Created += (_, e) => HandleJournalChange(e.FullPath);
        _journalWatcher.Changed += (_, e) => HandleJournalChange(e.FullPath);

        _pollTimer = new Timer(_ => PollJournals(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Start()
    {
        if (_journalWatcher == null) return;
        ReadStatusFile();
        InitializeOffsets();
        PollJournals();

        Task.Run(async () =>
        {
            await Task.Delay(3000);
            _silentReplay = false;
            _logger.Information("Silent replay finished, events active");
        });
    }

    private void InitializeOffsets()
    {
        lock (_readLock)
        {
            try
            {
                var files = Directory.GetFiles(_journalDirectory, "Journal.*.log");
                if (files.Length == 0) return;

                var latest = files.OrderByDescending(File.GetLastWriteTimeUtc).First();
                foreach (var file in files)
                {
                    if (file == latest)
                    {
                        _journalOffsets[file] = 0L;
                        _currentJournalFile = latest;
                        _logger.Information("Current journal: {File}", Path.GetFileName(latest));
                    }
                    else
                    {
                        _journalOffsets[file] = new FileInfo(file).Length;
                    }
                }
                _initialized = true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error initializing offsets");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Status.json
    // ─────────────────────────────────────────────────────────────

    private void ReadStatusFile()
    {
        var path = Path.Combine(_journalDirectory, "Status.json");
        if (!File.Exists(path)) return;

        StatusJson? status;
        try
        {
            var json = ReadFileWithRetry(path);
            if (string.IsNullOrEmpty(json)) return;
            status = JsonSerializer.Deserialize<StatusJson>(json);
        }
        catch (JsonException)
        {
            _logger.Debug("Status.json incomplete, will retry");
            return;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Error reading Status.json");
            return;
        }

        if (status == null) return;

        bool flagsChanged = false;
        bool fuelChanged = false;
        bool cargoChanged = false;
        bool balanceChanged = false;
        bool legalChanged = false;
        bool bodyChanged = false;
        bool pipsChanged = false;
        bool posChanged = false;
        uint oldFlags, oldFlags2;

        lock (_statusLock)
        {
            oldFlags = Status.Flags;
            oldFlags2 = Status.Flags2;

            if (Status.Flags != status.Flags) { Status.Flags = status.Flags; flagsChanged = true; }
            if (Status.Flags2 != (status.Flags2 ?? 0)) { Status.Flags2 = status.Flags2 ?? 0; flagsChanged = true; }

            if (Math.Abs(Status.FuelMain - (status.Fuel?.FuelMain ?? 0)) > 0.1 ||
                Math.Abs(Status.FuelReservoir - (status.Fuel?.FuelReservoir ?? 0)) > 0.1)
            {
                Status.FuelMain = status.Fuel?.FuelMain ?? 0;
                Status.FuelReservoir = status.Fuel?.FuelReservoir ?? 0;
                fuelChanged = true;
            }

            if (Math.Abs(Status.CargoTons - (status.Cargo ?? 0)) > 0.1)
            {
                Status.CargoTons = status.Cargo ?? 0;
                cargoChanged = true;
            }

            if (Status.Balance != (status.Balance ?? 0))
            {
                Status.Balance = status.Balance ?? 0;
                balanceChanged = true;
            }

            if (Status.LegalState != (status.LegalState ?? "Unknown"))
            {
                Status.LegalState = status.LegalState ?? "Unknown";
                legalChanged = true;
            }

            if (Status.Body != (status.BodyName ?? ""))
            {
                Status.Body = status.BodyName ?? "";
                bodyChanged = true;
            }

            if (status.Pips != null && status.Pips.Length >= 3)
            {
                if (Status.PipsSys != status.Pips[0] || Status.PipsEng != status.Pips[1] || Status.PipsWep != status.Pips[2])
                {
                    Status.PipsSys = status.Pips[0];
                    Status.PipsEng = status.Pips[1];
                    Status.PipsWep = status.Pips[2];
                    pipsChanged = true;
                }
            }

            // Posizione planetaria
            double lat = status.Latitude ?? 0;
            double lon = status.Longitude ?? 0;
            double alt = status.Altitude ?? 0;
            int head = status.Heading ?? 0;
            if (Math.Abs(Status.Latitude - lat) > 0.0001 || Math.Abs(Status.Longitude - lon) > 0.0001 ||
                Math.Abs(Status.Altitude - alt) > 0.1 || Status.Heading != head)
            {
                Status.Latitude = lat; Status.Longitude = lon;
                Status.Altitude = alt; Status.Heading = head;
                posChanged = true;
            }
        }

        if (_silentReplay) return;

        if (flagsChanged)
        {
            NotifyFlag(oldFlags, Status.Flags, 1u, DockedChanged, Status.IsDocked);
            NotifyFlag(oldFlags, Status.Flags, 1u << 1, LandedChanged, Status.IsLanded);
            NotifyFlag(oldFlags, Status.Flags, 1u << 2, LandingGearChanged, Status.LandingGearDown);
            NotifyFlag(oldFlags, Status.Flags, 1u << 3, ShieldsChanged, Status.ShieldsUp);
            NotifyFlag(oldFlags, Status.Flags, 1u << 4, SupercruiseChanged, Status.Supercruise);
            NotifyFlag(oldFlags, Status.Flags, 1u << 5, FlightAssistChanged, Status.FlightAssistOff);
            NotifyFlag(oldFlags, Status.Flags, 1u << 6, HardpointsChanged, Status.HardpointsDeployed);
            NotifyFlag(oldFlags, Status.Flags, 1u << 17, FsdChargingChanged, Status.FsdCharging);
            NotifyFlag(oldFlags, Status.Flags, 1u << 18, FsdCooldownChanged, Status.FsdCooldown);
            NotifyFlag(oldFlags, Status.Flags, 1u << 19, LowFuelChanged, Status.LowFuel);
            NotifyFlag(oldFlags, Status.Flags, 1u << 7, InWingChanged, Status.InWing);
            NotifyFlag(oldFlags, Status.Flags, 1u << 8, LightsOnChanged, Status.LightsOn);
            NotifyFlag(oldFlags, Status.Flags, 1u << 9, CargoScoopChanged, Status.CargoScoopDeployed);
            NotifyFlag(oldFlags, Status.Flags, 1u << 10, SilentRunningChanged, Status.SilentRunning);
            NotifyFlag(oldFlags, Status.Flags, 1u << 11, ScoopingFuelChanged, Status.ScoopingFuel);
            NotifyFlag(oldFlags, Status.Flags, 1u << 16, FsdMassLockedChanged, Status.FsdMassLocked);
            NotifyFlag(oldFlags, Status.Flags, 1u << 20, OverheatingChanged, Status.Overheating);
            NotifyFlag(oldFlags, Status.Flags, 1u << 22, InDangerChanged, Status.InDanger);
            NotifyFlag(oldFlags, Status.Flags, 1u << 23, BeingInterdictedChanged, Status.BeingInterdicted);
            NotifyFlag(oldFlags, Status.Flags, 1u << 24, InMainShipChanged, Status.InMainShip);
            NotifyFlag(oldFlags, Status.Flags, 1u << 25, InFighterChanged, Status.InFighter);
            NotifyFlag(oldFlags, Status.Flags, 1u << 26, InSrvChanged, Status.InSrv);

            // Flags2
            NotifyFlag(oldFlags2, Status.Flags2, 1u, OnFootChanged, Status.OnFoot);
            NotifyFlag(oldFlags2, Status.Flags2, 1u << 1, InTaxiChanged, Status.InTaxi);
            NotifyFlag(oldFlags2, Status.Flags2, 1u << 2, InMulticrewChanged, Status.InMulticrew);
            NotifyFlag(oldFlags2, Status.Flags2, 1u << 6, LowOxygenChanged, Status.LowOxygen);
            NotifyFlag(oldFlags2, Status.Flags2, 1u << 7, LowHealthChanged, Status.LowHealth);
        }

        if (fuelChanged) FuelChanged?.Invoke(this, (Status.FuelMain, Status.FuelReservoir, Status.FuelCapacity));
        if (cargoChanged) CargoChanged?.Invoke(this, (Status.CargoTons, Status.CargoCapacity));
        if (balanceChanged) BalanceChanged?.Invoke(this, Status.Balance);
        if (legalChanged) LegalStateChanged?.Invoke(this, Status.LegalState);
        if (bodyChanged) BodyNameChanged?.Invoke(this, Status.Body);
        if (pipsChanged) PipsChanged?.Invoke(this, (Status.PipsSys, Status.PipsEng, Status.PipsWep));
        if (posChanged) PositionChanged?.Invoke(this, (Status.Latitude, Status.Longitude, Status.Altitude, Status.Heading));
    }

    private void NotifyFlag(uint oldFlags, uint newFlags, uint mask,
        EventHandler<bool>? handler, bool newValue)
    {
        if (handler == null) return;
        bool wasSet = (oldFlags & mask) != 0;
        bool isSet = (newFlags & mask) != 0;
        if (wasSet != isSet) handler.Invoke(this, newValue);
    }

    // ─────────────────────────────────────────────────────────────
    // Journal
    // ─────────────────────────────────────────────────────────────

    private void HandleJournalChange(string filePath)
    {
        lock (_readLock)
        {
            if (string.CompareOrdinal(filePath, _currentJournalFile) > 0)
            {
                _currentJournalFile = filePath;
                _logger.Information("New current journal: {File}", Path.GetFileName(filePath));
            }
        }
        ReadJournalIncremental(filePath);
    }

    private void PollJournals()
    {
        if (_disposed) return;
        try
        {
            if (!_initialized) return;

            var files = Directory.GetFiles(_journalDirectory, "Journal.*.log");
            if (files.Length == 0) return;

            var latest = files.OrderByDescending(File.GetLastWriteTimeUtc).First();
            if (latest != _currentJournalFile)
            {
                _logger.Information("Current journal updated: {File}", Path.GetFileName(latest));
                _currentJournalFile = latest;
            }

            ReadJournalIncremental(_currentJournalFile);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error polling journals");
        }
    }

    private void ReadJournalIncremental(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;

        lock (_readLock)
        {
            try
            {
                long offset = _journalOffsets.GetOrAdd(filePath, 0L);

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length <= offset) return;

                int toRead = (int)(fs.Length - offset);
                var buffer = new byte[toRead];
                fs.Seek(offset, SeekOrigin.Begin);
                int read = fs.Read(buffer, 0, toRead);
                if (read <= 0) return;

                int lastNl = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
                if (lastNl < 0) return;

                _journalOffsets[filePath] = offset + lastNl + 1;

                var text = Encoding.UTF8.GetString(buffer, 0, lastNl + 1);
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    ProcessJournalLine(line);
                }
            }
            catch (IOException) { }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Error reading journal {File}", filePath);
            }
        }
    }

    private void ProcessJournalLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var evt)) return;
            var eventName = evt.GetString();

            switch (eventName)
            {
                case "FSDJump":
                    if (root.TryGetProperty("StarSystem", out var sys))
                    {
                        var name = sys.GetString() ?? "";
                        if (string.IsNullOrEmpty(name)) break;
                        lock (_statusLock) { Status.System = name; }
                        if (!_silentReplay) { _logger.Information("FSDJump -> {System}", name); SystemChanged?.Invoke(this, name); }
                    }
                    if (root.TryGetProperty("StarPos", out var pos) && pos.ValueKind == JsonValueKind.Array && pos.GetArrayLength() >= 3)
                    {
                        double x = pos[0].GetDouble(), y = pos[1].GetDouble(), z = pos[2].GetDouble();
                        lock (_statusLock) { Status.StarPosX = x; Status.StarPosY = y; Status.StarPosZ = z; }
                        if (!_silentReplay) StarPosChanged?.Invoke(this, (x, y, z));
                    }
                    break;

                case "Location":
                    {
                        string sysName = "", stName = "";
                        bool sysChanged = false, stChanged = false;
                        lock (_statusLock)
                        {
                            if (root.TryGetProperty("StarSystem", out var locSys))
                            {
                                sysName = locSys.GetString() ?? "";
                                if (!string.IsNullOrEmpty(sysName) && Status.System != sysName)
                                { Status.System = sysName; sysChanged = true; }
                            }
                            if (root.TryGetProperty("StationName", out var station))
                            {
                                stName = station.GetString() ?? "";
                                if (Status.Station != stName) { Status.Station = stName; stChanged = true; }
                            }
                            else
                            {
                                if (Status.Station != "") { Status.Station = ""; stName = ""; stChanged = true; }
                            }
                            if (root.TryGetProperty("StarPos", out var starPos) && starPos.ValueKind == JsonValueKind.Array && starPos.GetArrayLength() >= 3)
                            {
                                double x = starPos[0].GetDouble(), y = starPos[1].GetDouble(), z = starPos[2].GetDouble();
                                Status.StarPosX = x; Status.StarPosY = y; Status.StarPosZ = z;
                                if (!_silentReplay) StarPosChanged?.Invoke(this, (x, y, z));
                            }
                        }
                        if (_silentReplay) break;
                        if (sysChanged) { _logger.Information("Location -> {System}", sysName); SystemChanged?.Invoke(this, sysName); }
                        if (stChanged) { _logger.Information("Location -> station {Station}", stName); StationChanged?.Invoke(this, stName); }
                        break;
                    }

                case "Docked":
                    if (root.TryGetProperty("StationName", out var dockedStation))
                    {
                        var st = dockedStation.GetString() ?? "";
                        lock (_statusLock) { Status.Station = st; }
                        if (!_silentReplay) { _logger.Information("Docked -> {Station}", st); StationChanged?.Invoke(this, st); }
                    }
                    break;

                case "Undocked":
                    lock (_statusLock) { Status.Station = ""; }
                    if (!_silentReplay) { _logger.Information("Undocked"); StationChanged?.Invoke(this, ""); }
                    break;

                case "LoadGame":
                    {
                        string cmdr = "", ship = "";
                        long credits = 0;
                        lock (_statusLock)
                        {
                            if (root.TryGetProperty("Commander", out var c))
                                Status.Commander = cmdr = c.GetString() ?? "";
                            if (root.TryGetProperty("Ship", out var s))
                                Status.ShipType = ship = s.GetString() ?? "";
                            if (root.TryGetProperty("Credits", out var cr))
                                Status.Credits = credits = cr.GetInt64();
                        }
                        if (!_silentReplay && !string.IsNullOrEmpty(cmdr))
                        {
                            _logger.Information("LoadGame -> {Commander} ({Ship})", cmdr, ship);
                            CommanderChanged?.Invoke(this, (cmdr, ship));
                        }
                        break;
                    }

                case "Loadout":
                    {
                        string ship = "", name = "", ident = "";
                        double cap = 0, cargoCap = 0, jumpRange = 0, hull = 1;
                        long rebuy = 0;
                        int modules = 0;
                        lock (_statusLock)
                        {
                            if (root.TryGetProperty("Ship", out var s))
                                Status.ShipType = ship = s.GetString() ?? "";
                            if (root.TryGetProperty("ShipName", out var sn))
                                Status.ShipName = name = sn.GetString() ?? "";
                            if (root.TryGetProperty("ShipIdent", out var si))
                                Status.ShipIdent = ident = si.GetString() ?? "";
                            if (root.TryGetProperty("FuelCapacity", out var fc) && fc.TryGetProperty("Main", out var mainCap))
                            { cap = mainCap.GetDouble(); Status.FuelCapacity = cap; }
                            if (root.TryGetProperty("CargoCapacity", out var cc))
                            { cargoCap = cc.GetDouble(); Status.CargoCapacity = cargoCap; }
                            if (root.TryGetProperty("MaxJumpRange", out var mjr))
                            { jumpRange = mjr.GetDouble(); Status.MaxJumpRange = jumpRange; }
                            if (root.TryGetProperty("HullHealth", out var hh))
                            { hull = hh.GetDouble(); Status.HullHealth = hull; }
                            if (root.TryGetProperty("Rebuy", out var rb))
                            { rebuy = rb.GetInt64(); Status.RebuyCost = rebuy; }
                            if (root.TryGetProperty("Modules", out var mods) && mods.ValueKind == JsonValueKind.Array)
                            { modules = mods.GetArrayLength(); Status.ModuleCount = modules; }
                        }
                        if (!_silentReplay && !string.IsNullOrEmpty(ship))
                        {
                            _logger.Information("Loadout -> {Ship} '{Name}' ({Ident})", ship, name, ident);
                            ShipChanged?.Invoke(this, (ship, name, ident));
                        }
                        break;
                    }

                case "Rank":
                    {
                        int c = 0, t = 0, e = 0, emp = 0, fed = 0, cqc = 0;
                        lock (_statusLock)
                        {
                            if (root.TryGetProperty("Combat", out var v)) Status.RankCombat = c = v.GetInt32();
                            if (root.TryGetProperty("Trade", out v)) Status.RankTrade = t = v.GetInt32();
                            if (root.TryGetProperty("Explore", out v)) Status.RankExplore = e = v.GetInt32();
                            if (root.TryGetProperty("Empire", out v)) Status.RankEmpire = emp = v.GetInt32();
                            if (root.TryGetProperty("Federation", out v)) Status.RankFederation = fed = v.GetInt32();
                            if (root.TryGetProperty("CQC", out v)) Status.RankCqc = cqc = v.GetInt32();
                        }
                        if (!_silentReplay) RankChanged?.Invoke(this, (c, t, e, emp, fed, cqc));
                        break;
                    }

                case "Progress":
                    {
                        int c = 0, t = 0, e = 0, emp = 0, fed = 0, cqc = 0;
                        lock (_statusLock)
                        {
                            if (root.TryGetProperty("Combat", out var v)) Status.ProgressCombat = c = v.GetInt32();
                            if (root.TryGetProperty("Trade", out v)) Status.ProgressTrade = t = v.GetInt32();
                            if (root.TryGetProperty("Explore", out v)) Status.ProgressExplore = e = v.GetInt32();
                            if (root.TryGetProperty("Empire", out v)) Status.ProgressEmpire = emp = v.GetInt32();
                            if (root.TryGetProperty("Federation", out v)) Status.ProgressFederation = fed = v.GetInt32();
                            if (root.TryGetProperty("CQC", out v)) Status.ProgressCqc = cqc = v.GetInt32();
                        }
                        if (!_silentReplay) ProgressChanged?.Invoke(this, (c, t, e, emp, fed, cqc));
                        break;
                    }

                case "UnderAttack":
                    lock (_statusLock) { Status.UnderAttack = true; }
                    if (!_silentReplay) { _logger.Information("UnderAttack"); UnderAttackChanged?.Invoke(this, true); }
                    break;

                case "Interdicted":
                    lock (_statusLock) { Status.Interdicted = true; }
                    if (!_silentReplay) { _logger.Information("Interdicted"); InterdictedChanged?.Invoke(this, true); }
                    break;

                case "EscapeInterdiction":
                    lock (_statusLock) { Status.Interdicted = false; Status.EscapedInterdiction = true; }
                    if (!_silentReplay) { _logger.Information("EscapeInterdiction"); EscapedInterdictionChanged?.Invoke(this, true); }
                    break;

                case "Bounty":
                    if (root.TryGetProperty("Reward", out var rew))
                    {
                        long r = rew.GetInt64();
                        lock (_statusLock) { Status.LastBounty = r; }
                        if (!_silentReplay) { _logger.Information("Bounty -> {Reward}", r); BountyChanged?.Invoke(this, r); }
                    }
                    break;

                case "HullDamage":
                    if (root.TryGetProperty("Health", out var hlt))
                    {
                        double h = hlt.GetDouble();
                        lock (_statusLock) { Status.HullDamage = h; }
                        if (!_silentReplay) HullDamageChanged?.Invoke(this, h);
                    }
                    break;

                case "MissionAccepted":
                    {
                        string name = "";
                        lock (_statusLock)
                        {
                            if (root.TryGetProperty("Name", out var n)) Status.LastMissionName = name = n.GetString() ?? "";
                            Status.MissionCount++;
                        }
                        if (!_silentReplay)
                        {
                            if (!string.IsNullOrEmpty(name)) MissionNameChanged?.Invoke(this, name);
                            MissionCountChanged?.Invoke(this, Status.MissionCount);
                        }
                        break;
                    }

                case "MissionCompleted":
                    {
                        long reward = 0;
                        lock (_statusLock)
                        {
                            if (root.TryGetProperty("Reward", out var r)) Status.LastMissionReward = reward = r.GetInt64();
                            if (Status.MissionCount > 0) Status.MissionCount--;
                        }
                        if (!_silentReplay)
                        {
                            if (reward > 0) MissionRewardChanged?.Invoke(this, reward);
                            MissionCountChanged?.Invoke(this, Status.MissionCount);
                        }
                        break;
                    }

                case "Scan":
                    {
                        string body = "", type = "";
                        lock (_statusLock)
                        {
                            if (root.TryGetProperty("BodyName", out var b)) Status.LastScanBody = body = b.GetString() ?? "";
                            if (root.TryGetProperty("StarType", out var st)) Status.LastScanType = type = st.GetString() ?? "";
                            else if (root.TryGetProperty("PlanetClass", out var pc)) Status.LastScanType = type = pc.GetString() ?? "";
                        }
                        if (!_silentReplay && !string.IsNullOrEmpty(body))
                        {
                            _logger.Information("Scan -> {Body} ({Type})", body, type);
                            ScanBodyChanged?.Invoke(this, body);
                            ScanTypeChanged?.Invoke(this, type);
                        }
                        break;
                    }

                case "MaterialCollected":
                    if (root.TryGetProperty("Name", out var mat))
                    {
                        string m = mat.GetString() ?? "";
                        lock (_statusLock) { Status.LastMaterial = m; }
                        if (!_silentReplay && !string.IsNullOrEmpty(m)) MaterialChanged?.Invoke(this, m);
                    }
                    break;

                case "FSSDiscoveryScan":
                    if (root.TryGetProperty("BodyCount", out var bc))
                    {
                        int n = bc.GetInt32();
                        lock (_statusLock) { Status.FssBodies = n; }
                        if (!_silentReplay) FssBodiesChanged?.Invoke(this, n);
                    }
                    break;

                case "MarketSell":
                    if (root.TryGetProperty("TotalSale", out var ts))
                    {
                        long p = ts.GetInt64();
                        lock (_statusLock) { Status.LastMarketProfit = p; }
                        if (!_silentReplay) MarketProfitChanged?.Invoke(this, p);
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            _logger.Debug("Invalid journal line: {Line}", line);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Error in ProcessJournalLine");
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Utility
    // ─────────────────────────────────────────────────────────────

    private static string ReadFileWithRetry(string path, int retries = 5)
    {
        for (int i = 0; i < retries; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (IOException) { Thread.Sleep(50); }
        }
        return "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _folderRetryTimer?.Dispose();
        _pollTimer?.Dispose();
        _statusWatcher?.Dispose();
        _journalWatcher?.Dispose();
    }

    private sealed class StatusJson
    {
        public uint Flags { get; set; }
        public uint? Flags2 { get; set; }
        public FuelJson? Fuel { get; set; }
        public string? LegalState { get; set; }
        public string? BodyName { get; set; }
        public long? Balance { get; set; }
        public double? Cargo { get; set; }
        public int[]? Pips { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? Altitude { get; set; }
        public int? Heading { get; set; }
    }

    private sealed class FuelJson
    {
        public double FuelMain { get; set; }
        public double FuelReservoir { get; set; }
    }
}