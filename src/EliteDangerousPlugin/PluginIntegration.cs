using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using MacroDeck.Localization;   // <-- NUOVO
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Variables;
using Serilog;

namespace EliteDangerousPlugin;

public sealed class PluginIntegration : IPluginIntegration, IDisposable
{
    private readonly ILogger _logger;
    private readonly EliteJournalWatcher _watcher;
    private readonly ConcurrentDictionary<string, Guid> _variableIds = new();
    private readonly CancellationTokenSource _cts = new();

    // Coda di scrittura: un solo consumatore, coalescing per variabile
    private readonly Channel<(string Name, string Value)> _writes =
        Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly Task _writerTask;
    private IIntegrationContext? _context;
    private bool _disposed;

    private static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public PluginIntegration(ILogger logger)
    {
        _logger = logger.ForContext<PluginIntegration>();
        _watcher = new EliteJournalWatcher(logger);

        // ─── Journal ─────────────────────────────────────────────
        _watcher.SystemChanged += (_, name) => Enqueue("ed_current_system", name);
        _watcher.StationChanged += (_, station) => Enqueue("ed_current_station", station);

        _watcher.CommanderChanged += (_, e) =>
        {
            Enqueue("ed_commander", e.Commander);
            Enqueue("ed_ship_type", e.Ship);
        };

        _watcher.ShipChanged += (_, e) =>
        {
            Enqueue("ed_ship_type", e.Ship);
            Enqueue("ed_ship_name", e.Name);
        };

        // ─── Status.json: flag esistenti ─────────────────────────
        _watcher.DockedChanged += (_, v) => Enqueue("ed_is_docked", Bool(v));
        _watcher.LandedChanged += (_, v) => Enqueue("ed_is_landed", Bool(v));
        _watcher.LandingGearChanged += (_, v) => Enqueue("ed_landing_gear_down", Bool(v));
        _watcher.ShieldsChanged += (_, v) => Enqueue("ed_shields_up", Bool(v));
        _watcher.SupercruiseChanged += (_, v) => Enqueue("ed_supercruise", Bool(v));
        _watcher.FlightAssistChanged += (_, v) => Enqueue("ed_flight_assist_off", Bool(v));
        _watcher.HardpointsChanged += (_, v) => Enqueue("ed_hardpoints_deployed", Bool(v));
        _watcher.FsdChargingChanged += (_, v) => Enqueue("ed_fsd_charging", Bool(v));
        _watcher.FsdCooldownChanged += (_, v) => Enqueue("ed_fsd_cooldown", Bool(v));
        _watcher.LowFuelChanged += (_, v) => Enqueue("ed_low_fuel", Bool(v));

        // ─── Status.json: nuove 12 flag ──────────────────────────
        _watcher.InWingChanged += (_, v) => Enqueue("ed_in_wing", Bool(v));
        _watcher.LightsOnChanged += (_, v) => Enqueue("ed_lights_on", Bool(v));
        _watcher.CargoScoopChanged += (_, v) => Enqueue("ed_cargo_scoop_deployed", Bool(v));
        _watcher.SilentRunningChanged += (_, v) => Enqueue("ed_silent_running", Bool(v));
        _watcher.ScoopingFuelChanged += (_, v) => Enqueue("ed_scooping_fuel", Bool(v));
        _watcher.FsdMassLockedChanged += (_, v) => Enqueue("ed_fsd_mass_locked", Bool(v));
        _watcher.OverheatingChanged += (_, v) => Enqueue("ed_overheating", Bool(v));
        _watcher.InDangerChanged += (_, v) => Enqueue("ed_in_danger", Bool(v));
        _watcher.BeingInterdictedChanged += (_, v) => Enqueue("ed_being_interdicted", Bool(v));
        _watcher.InMainShipChanged += (_, v) => Enqueue("ed_in_main_ship", Bool(v));
        _watcher.InFighterChanged += (_, v) => Enqueue("ed_in_fighter", Bool(v));
        _watcher.InSrvChanged += (_, v) => Enqueue("ed_in_srv", Bool(v));

        // ─── Status.json: valori ─────────────────────────────────
        _watcher.FuelChanged += (_, f) =>
        {
            Enqueue("ed_fuel_main", f.Main.ToString("F1", Inv));
            Enqueue("ed_fuel_reservoir", f.Reservoir.ToString("F1", Inv));
            Enqueue("ed_total_fuel", (f.Main + f.Reservoir).ToString("F1", Inv));
            if (f.Capacity > 0.01)
                Enqueue("ed_fuel_percent", ((f.Main + f.Reservoir) / f.Capacity * 100).ToString("F0", Inv));
        };

        _watcher.CargoChanged += (_, cargo) => Enqueue("ed_cargo", cargo.ToString("F1", Inv));
        _watcher.BalanceChanged += (_, b) => Enqueue("ed_balance", b.ToString(Inv));
        _watcher.LegalStateChanged += (_, s) => Enqueue("ed_legal_state", s);
        _watcher.BodyNameChanged += (_, s) => Enqueue("ed_body_name", s);

        _watcher.PipsChanged += (_, p) =>
        {
            Enqueue("ed_pips_sys", p.Sys.ToString(Inv));
            Enqueue("ed_pips_eng", p.Eng.ToString(Inv));
            Enqueue("ed_pips_wep", p.Wep.ToString(Inv));
        };

        // Azione utile che sostituisce LogMessageAction
        Actions = [new ResyncAction(this)];

        // Writer task: consuma la coda, una scrittura alla volta
        _writerTask = Task.Run(() => WriterLoopAsync(_cts.Token));
    }

    public IReadOnlyList<IActionDefinition> Actions { get; }

    // ─────────────────────────────────────────────────────────────
    // IPluginIntegration
    // ─────────────────────────────────────────────────────────────

    public async Task InitializeAsync(IIntegrationContext context)
    {
        _context = context;
        _logger.Information("Starting Elite Dangerous watcher...");

        await EnsureVariablesAsync(context);
        _watcher.Start();
        await PushAllAsync();
    }

    public async Task ShutdownAsync()
    {
        await DisposeAsync();
    }

    public void Dispose()
    {
        _ = DisposeAsync();
    }

    private async Task DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _writes.Writer.TryComplete();
        _cts.Cancel();

        try { await _writerTask; } catch { }

        _watcher.Dispose();
        _cts.Dispose();
    }

    // ─────────────────────────────────────────────────────────────
    // Azione: Risincronizza variabili
    // ─────────────────────────────────────────────────────────────

    public async Task ResyncAsync()
    {
        _logger.Information("Manual resync requested");
        await PushAllAsync();
    }

    private sealed class ResyncAction : IActionDefinition
    {
        private readonly PluginIntegration _owner;
        public ResyncAction(PluginIntegration owner) => _owner = owner;

        public string Id => "resync-variables";
        public LocalizedText Name => "Elite Dangerous: Resync variables";
        public LocalizedText Description => "Re-reads Status.json and journals and refreshes all variables.";
        public IReadOnlyList<ActionParameter> Parameters => [];
        public MacroDeckPlatform Platforms => MacroDeckPlatform.All;
        public IActionExecutor CreateExecutor() => new Executor(_owner);

        private sealed class Executor : IActionExecutor
        {
            private readonly PluginIntegration _owner;
            public Executor(PluginIntegration owner) => _owner = owner;

            public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
            {
                _ = _owner.ResyncAsync();
                return ActionResult.SucceededTask;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Variabili
    // ─────────────────────────────────────────────────────────────

    private static readonly (string Name, VariableType Type)[] Definitions =
    [
        ("ed_current_system",  VariableType.Text),
        ("ed_current_station", VariableType.Text),
        ("ed_legal_state",     VariableType.Text),
        ("ed_body_name",       VariableType.Text),
        ("ed_commander",       VariableType.Text),
        ("ed_ship_type",       VariableType.Text),
        ("ed_ship_name",       VariableType.Text),
        ("ed_cargo",           VariableType.Text),
        ("ed_fuel_main",       VariableType.Text),
        ("ed_fuel_reservoir",  VariableType.Text),
        ("ed_total_fuel",      VariableType.Text),
        ("ed_fuel_percent",    VariableType.Text),
        ("ed_balance",         VariableType.Text),
        ("ed_is_docked",           VariableType.Text),
        ("ed_is_landed",           VariableType.Text),
        ("ed_landing_gear_down",   VariableType.Text),
        ("ed_shields_up",          VariableType.Text),
        ("ed_supercruise",         VariableType.Text),
        ("ed_hardpoints_deployed", VariableType.Text),
        ("ed_low_fuel",            VariableType.Text),
        ("ed_flight_assist_off",   VariableType.Text),
        ("ed_fsd_charging",        VariableType.Text),
        ("ed_fsd_cooldown",        VariableType.Text),
        ("ed_in_wing",             VariableType.Text),
        ("ed_lights_on",           VariableType.Text),
        ("ed_cargo_scoop_deployed", VariableType.Text),
        ("ed_silent_running",      VariableType.Text),
        ("ed_scooping_fuel",       VariableType.Text),
        ("ed_fsd_mass_locked",     VariableType.Text),
        ("ed_overheating",         VariableType.Text),
        ("ed_in_danger",           VariableType.Text),
        ("ed_being_interdicted",   VariableType.Text),
        ("ed_in_main_ship",        VariableType.Text),
        ("ed_in_fighter",          VariableType.Text),
        ("ed_in_srv",              VariableType.Text),
        ("ed_pips_sys",            VariableType.Text),
        ("ed_pips_eng",            VariableType.Text),
        ("ed_pips_wep",            VariableType.Text),
    ];

    private async Task EnsureVariablesAsync(IIntegrationContext context)
    {
        foreach (var (name, type) in Definitions)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var existing = await context.Variables.GetByNameAsync(name);
                    if (existing is null)
                    {
                        var created = await context.Variables.CreateAsync(name, type);
                        _variableIds[name] = created.Id;
                        _logger.Information("Variable created: {Name} ({Type})", name, type);
                    }
                    else
                    {
                        _variableIds[name] = existing.Id;
                    }
                    await Task.Delay(80);
                    break;
                }
                catch (Exception ex) when (ex.Message.Contains("too quickly") && attempt < 2)
                {
                    await Task.Delay(RetryDelay);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error creating {Name}", name);
                    await Task.Delay(80);
                    break;
                }
            }
        }
        _logger.Information("Initialized {Count} variables", _variableIds.Count);
    }

    // ─────────────────────────────────────────────────────────────
    // Coda di scrittura
    // ─────────────────────────────────────────────────────────────

    private void Enqueue(string name, string value)
    {
        if (_disposed) return;
        _writes.Writer.TryWrite((name, value));
    }

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        var pending = new Dictionary<string, string>();
        try
        {
            while (await _writes.Reader.WaitToReadAsync(ct))
            {
                while (_writes.Reader.TryRead(out var w))
                    pending[w.Name] = w.Value;   // coalescing: tieni solo l'ultimo

                foreach (var (name, value) in pending)
                    await WriteWithRetryAsync(name, value, ct);

                pending.Clear();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Error(ex, "Writer loop crashed");
        }
    }

    private async Task WriteWithRetryAsync(string name, string value, CancellationToken ct)
    {
        if (!_variableIds.TryGetValue(name, out var id)) return;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await _context!.Variables.SetValueAsync(id, value);
                await Task.Delay(WriteDelay, ct);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex.Message.Contains("too quickly") && attempt < 2)
            {
                await Task.Delay(RetryDelay, ct);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Error setting {Name}", name);
                return;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Push iniziale (diretto, non passa dalla coda)
    // ─────────────────────────────────────────────────────────────

    private async Task PushAllAsync()
    {
        var ctx = _context;
        if (ctx == null) return;

        var s = _watcher.Status;
        var list = new List<(string Name, string Value)>
        {
            ("ed_current_system", s.CurrentSystem),
            ("ed_current_station", s.CurrentStation),
            ("ed_legal_state", s.LegalState),
            ("ed_body_name", s.BodyName),
            ("ed_commander", s.Commander),
            ("ed_ship_type", s.ShipType),
            ("ed_ship_name", s.ShipName),
            ("ed_cargo", s.Cargo.ToString("F1", Inv)),
            ("ed_fuel_main", s.FuelMain.ToString("F1", Inv)),
            ("ed_fuel_reservoir", s.FuelReservoir.ToString("F1", Inv)),
            ("ed_total_fuel", s.TotalFuel.ToString("F1", Inv)),
            ("ed_fuel_percent", s.FuelPercent.ToString("F0", Inv)),
            ("ed_balance", s.Balance.ToString(Inv)),
            ("ed_is_docked", Bool(s.IsDocked)),
            ("ed_is_landed", Bool(s.IsLanded)),
            ("ed_landing_gear_down", Bool(s.LandingGearDown)),
            ("ed_shields_up", Bool(s.ShieldsUp)),
            ("ed_supercruise", Bool(s.Supercruise)),
            ("ed_hardpoints_deployed", Bool(s.HardpointsDeployed)),
            ("ed_low_fuel", Bool(s.LowFuel)),
            ("ed_flight_assist_off", Bool(s.FlightAssistOff)),
            ("ed_fsd_charging", Bool(s.FsdCharging)),
            ("ed_fsd_cooldown", Bool(s.FsdCooldown)),
            ("ed_in_wing", Bool(s.InWing)),
            ("ed_lights_on", Bool(s.LightsOn)),
            ("ed_cargo_scoop_deployed", Bool(s.CargoScoopDeployed)),
            ("ed_silent_running", Bool(s.SilentRunning)),
            ("ed_scooping_fuel", Bool(s.ScoopingFuel)),
            ("ed_fsd_mass_locked", Bool(s.FsdMassLocked)),
            ("ed_overheating", Bool(s.Overheating)),
            ("ed_in_danger", Bool(s.InDanger)),
            ("ed_being_interdicted", Bool(s.BeingInterdicted)),
            ("ed_in_main_ship", Bool(s.InMainShip)),
            ("ed_in_fighter", Bool(s.InFighter)),
            ("ed_in_srv", Bool(s.InSrv)),
            ("ed_pips_sys", s.PipsSys.ToString(Inv)),
            ("ed_pips_eng", s.PipsEng.ToString(Inv)),
            ("ed_pips_wep", s.PipsWep.ToString(Inv)),
        };

        foreach (var (name, value) in list)
        {
            if (!_variableIds.TryGetValue(name, out var id)) continue;
            try
            {
                await ctx.Variables.SetValueAsync(id, value);
                await Task.Delay(WriteDelay, _cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Initial push failed for {Name}", name);
            }
        }
    }

    private static string Bool(bool value) => value ? "true" : "false";
}