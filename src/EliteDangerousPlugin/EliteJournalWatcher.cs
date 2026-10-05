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
    private bool _silentReplay = true;    // il primo giro non emette eventi
    private bool _disposed;

    public EliteStatusModel Status { get; } = new();

    // ─── Eventi da journal ───────────────────────────────────────
    public event EventHandler<string>? SystemChanged;
    public event EventHandler<string>? StationChanged;
    public event EventHandler<(string Commander, string Ship)>? CommanderChanged;
    public event EventHandler<(string Ship, string Name)>? ShipChanged;

    // ─── Eventi da Status.json: flag esistenti ──────────────────
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

    // ─── Eventi da Status.json: nuove 12 flag ───────────────────
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

    // ─── Eventi da Status.json: valori ──────────────────────────
    public event EventHandler<string>? LegalStateChanged;
    public event EventHandler<string>? BodyNameChanged;
    public event EventHandler<(double Main, double Reservoir, double Capacity)>? FuelChanged;
    public event EventHandler<double>? CargoChanged;
    public event EventHandler<long>? BalanceChanged;
    public event EventHandler<(int Sys, int Eng, int Wep)>? PipsChanged;

    public EliteJournalWatcher(ILogger logger)
    {
        _logger = logger.ForContext<EliteJournalWatcher>();
        _journalDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Saved Games", "Frontier Developments", "Elite Dangerous");

        if (!Directory.Exists(_journalDirectory))
        {
            _logger.Warning("Journal directory not found: {Dir} - will retry", _journalDirectory);
            // Riprova ogni 30 secondi finché la cartella non appare
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

        // Dopo il primo giro, disattiva il replay silenzioso
        // e fai un ultimo push "vero" per allineare le variabili.
        Task.Run(async () =>
        {
            await Task.Delay(3000);
            _silentReplay = false;
            _logger.Information("Replay silenzioso terminato, eventi attivi");
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
                        _logger.Information("Journal corrente: {File}", Path.GetFileName(latest));
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
                _logger.Error(ex, "Errore nell'inizializzazione degli offset");
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

        StatusJson? status = null;
        try
        {
            var json = ReadFileWithRetry(path);
            if (string.IsNullOrEmpty(json)) return;
            status = JsonSerializer.Deserialize<StatusJson>(json);
        }
        catch (JsonException)
        {
            // Status.json troncato durante la riscrittura: riprova al prossimo giro
            _logger.Debug("Status.json incompleto, riprovo");
            return;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Errore lettura Status.json");
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

        uint oldFlags = 0;
        lock (_statusLock)
        {
            oldFlags = Status.Flags;

            if (Status.Flags != status.Flags) { Status.Flags = status.Flags; flagsChanged = true; }
            if (Status.Flags2 != (status.Flags2 ?? 0)) Status.Flags2 = status.Flags2 ?? 0;

            if (Math.Abs(Status.FuelMain - (status.Fuel?.FuelMain ?? 0)) > 0.1 ||
                Math.Abs(Status.FuelReservoir - (status.Fuel?.FuelReservoir ?? 0)) > 0.1)
            {
                Status.FuelMain = status.Fuel?.FuelMain ?? 0;
                Status.FuelReservoir = status.Fuel?.FuelReservoir ?? 0;
                fuelChanged = true;
            }

            if (Math.Abs(Status.Cargo - (status.Cargo ?? 0)) > 0.1)
            {
                Status.Cargo = status.Cargo ?? 0;
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

            if (Status.BodyName != (status.BodyName ?? ""))
            {
                Status.BodyName = status.BodyName ?? "";
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
        }

        // Durante il replay silenzioso, niente eventi: solo stato aggiornato
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
        }

        if (fuelChanged) FuelChanged?.Invoke(this, (Status.FuelMain, Status.FuelReservoir, Status.FuelCapacity));
        if (cargoChanged) CargoChanged?.Invoke(this, Status.Cargo);
        if (balanceChanged) BalanceChanged?.Invoke(this, Status.Balance);
        if (legalChanged) LegalStateChanged?.Invoke(this, Status.LegalState);
        if (bodyChanged) BodyNameChanged?.Invoke(this, Status.BodyName);
        if (pipsChanged) PipsChanged?.Invoke(this, (Status.PipsSys, Status.PipsEng, Status.PipsWep));
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
                _logger.Information("Nuovo journal corrente: {File}", Path.GetFileName(filePath));
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
                _logger.Information("Journal corrente aggiornato: {File}", Path.GetFileName(latest));
                _currentJournalFile = latest;
            }

            ReadJournalIncremental(_currentJournalFile);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Errore nel polling del journal");
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
                if (lastNl < 0)
                {
                    // Riga incompleta: riprova al prossimo giro
                    return;
                }

                _journalOffsets[filePath] = offset + lastNl + 1;

                var text = Encoding.UTF8.GetString(buffer, 0, lastNl + 1);
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    ProcessJournalLine(line);
                }
            }
            catch (IOException) { /* file bloccato, riprova */ }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Errore leggendo journal {File}", filePath);
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
                        if (string.IsNullOrEmpty(name)) return;
                        lock (_statusLock) { Status.CurrentSystem = name; }
                        if (!_silentReplay)
                        {
                            _logger.Information("FSDJump -> {System}", name);
                            SystemChanged?.Invoke(this, name);
                        }
                    }
                    break;

                case "Location":
                    {
                        bool sysChanged = false, stChanged = false;
                        string sysName = "", stName = "";
                        lock (_statusLock)
                        {
                            if (root.TryGetProperty("StarSystem", out var locSys))
                            {
                                sysName = locSys.GetString() ?? "";
                                if (!string.IsNullOrEmpty(sysName) && Status.CurrentSystem != sysName)
                                {
                                    Status.CurrentSystem = sysName;
                                    sysChanged = true;
                                }
                            }
                            if (root.TryGetProperty("StationName", out var station))
                            {
                                stName = station.GetString() ?? "";
                                if (Status.CurrentStation != stName)
                                {
                                    Status.CurrentStation = stName;
                                    stChanged = true;
                                }
                            }
                            else
                            {
                                // Sei nello spazio: azzera la stazione
                                if (Status.CurrentStation != "")
                                {
                                    Status.CurrentStation = "";
                                    stName = "";
                                    stChanged = true;
                                }
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
                        lock (_statusLock) { Status.CurrentStation = st; }
                        if (!_silentReplay)
                        {
                            _logger.Information("Docked -> {Station}", st);
                            StationChanged?.Invoke(this, st);
                        }
                    }
                    break;

                case "Undocked":
                    lock (_statusLock) { Status.CurrentStation = ""; }
                    if (!_silentReplay)
                    {
                        _logger.Information("Undocked");
                        StationChanged?.Invoke(this, "");
                    }
                    break;

                case "LoadGame":
                    {
                        string cmdr = "", ship = "";
                        lock (_statusLock)
                        {
                            if (root.TryGetProperty("Commander", out var c))
                                Status.Commander = cmdr = c.GetString() ?? "";
                            if (root.TryGetProperty("Ship", out var s))
                                Status.ShipType = ship = s.GetString() ?? "";
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
                        string ship = "", name = "";
                        double capacity = 0;
                        lock (_statusLock)
                        {
                            if (root.TryGetProperty("Ship", out var s))
                                Status.ShipType = ship = s.GetString() ?? "";
                            if (root.TryGetProperty("ShipName", out var sn))
                                Status.ShipName = name = sn.GetString() ?? "";
                            if (root.TryGetProperty("FuelCapacity", out var fc) &&
                                fc.TryGetProperty("Main", out var mainCap))
                            {
                                capacity = mainCap.GetDouble();
                                Status.FuelCapacity = capacity;
                            }
                        }
                        if (!_silentReplay && !string.IsNullOrEmpty(ship))
                        {
                            _logger.Information("Loadout -> {Ship} '{Name}', capacity {Cap}", ship, name, capacity);
                            ShipChanged?.Invoke(this, (ship, name));
                        }
                        break;
                    }
            }
        }
        catch (JsonException)
        {
            // Riga malformata: logga in Debug, non nascondere altri errori
            _logger.Debug("Riga journal non valida: {Line}", line);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Errore in ProcessJournalLine");
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
    }

    private sealed class FuelJson
    {
        public double FuelMain { get; set; }
        public double FuelReservoir { get; set; }
    }
}