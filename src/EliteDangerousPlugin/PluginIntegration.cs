using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using MacroDeck.Localization;
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

        // ─── Posizione ───────────────────────────────────────────
        _watcher.SystemChanged += (_, v) => Enqueue("ed_pos_system", v);
        _watcher.StationChanged += (_, v) => Enqueue("ed_pos_station", v);
        _watcher.BodyNameChanged += (_, v) => Enqueue("ed_pos_body", v);
        _watcher.PositionChanged += (_, p) =>
        {
            Enqueue("ed_pos_latitude", p.Lat.ToString("F6", Inv));
            Enqueue("ed_pos_longitude", p.Lon.ToString("F6", Inv));
            Enqueue("ed_pos_altitude", p.Alt.ToString("F1", Inv));
            Enqueue("ed_pos_heading", p.Head.ToString(Inv));
        };
        _watcher.StarPosChanged += (_, p) =>
        {
            Enqueue("ed_pos_starpos_x", p.X.ToString("F3", Inv));
            Enqueue("ed_pos_starpos_y", p.Y.ToString("F3", Inv));
            Enqueue("ed_pos_starpos_z", p.Z.ToString("F3", Inv));
        };

        // ─── Nave ────────────────────────────────────────────────
        _watcher.CommanderChanged += (_, e) =>
        {
            Enqueue("ed_commander_name", e.Commander);
            Enqueue("ed_ship_type", e.Ship);
        };
        _watcher.ShipChanged += (_, e) =>
        {
            Enqueue("ed_ship_type", e.Ship);
            Enqueue("ed_ship_name", e.Name);
            Enqueue("ed_ship_ident", e.Ident);
        };

        // ─── Stato (Flags) ───────────────────────────────────────
        _watcher.DockedChanged += (_, v) => Enqueue("ed_status_docked", Bool(v));
        _watcher.LandedChanged += (_, v) => Enqueue("ed_status_landed", Bool(v));
        _watcher.LandingGearChanged += (_, v) => Enqueue("ed_status_gear_down", Bool(v));
        _watcher.ShieldsChanged += (_, v) => Enqueue("ed_status_shields", Bool(v));
        _watcher.SupercruiseChanged += (_, v) => Enqueue("ed_status_supercruise", Bool(v));
        _watcher.FlightAssistChanged += (_, v) => Enqueue("ed_status_fa_off", Bool(v));
        _watcher.HardpointsChanged += (_, v) => Enqueue("ed_status_hardpoints", Bool(v));
        _watcher.InWingChanged += (_, v) => Enqueue("ed_status_wing", Bool(v));
        _watcher.LightsOnChanged += (_, v) => Enqueue("ed_status_lights", Bool(v));
        _watcher.CargoScoopChanged += (_, v) => Enqueue("ed_status_cargo_scoop", Bool(v));
        _watcher.SilentRunningChanged += (_, v) => Enqueue("ed_status_silent", Bool(v));
        _watcher.ScoopingFuelChanged += (_, v) => Enqueue("ed_status_scooping", Bool(v));
        _watcher.FsdMassLockedChanged += (_, v) => Enqueue("ed_status_fsd_masslock", Bool(v));
        _watcher.FsdChargingChanged += (_, v) => Enqueue("ed_status_fsd_charging", Bool(v));
        _watcher.FsdCooldownChanged += (_, v) => Enqueue("ed_status_fsd_cooldown", Bool(v));
        _watcher.LowFuelChanged += (_, v) => Enqueue("ed_status_low_fuel", Bool(v));
        _watcher.OverheatingChanged += (_, v) => Enqueue("ed_status_overheat", Bool(v));
        _watcher.InDangerChanged += (_, v) => Enqueue("ed_status_danger", Bool(v));
        _watcher.BeingInterdictedChanged += (_, v) => Enqueue("ed_status_interdicted", Bool(v));
        _watcher.InMainShipChanged += (_, v) => Enqueue("ed_status_main_ship", Bool(v));
        _watcher.InFighterChanged += (_, v) => Enqueue("ed_status_fighter", Bool(v));
        _watcher.InSrvChanged += (_, v) => Enqueue("ed_status_srv", Bool(v));

        // ─── Flags2 (Odyssey) ────────────────────────────────────
        _watcher.OnFootChanged += (_, v) => Enqueue("ed_status_on_foot", Bool(v));
        _watcher.InTaxiChanged += (_, v) => Enqueue("ed_status_in_taxi", Bool(v));
        _watcher.InMulticrewChanged += (_, v) => Enqueue("ed_status_multicrew", Bool(v));
        _watcher.LowOxygenChanged += (_, v) => Enqueue("ed_status_low_oxygen", Bool(v));
        _watcher.LowHealthChanged += (_, v) => Enqueue("ed_status_low_health", Bool(v));

        // ─── Carburante ──────────────────────────────────────────
        _watcher.FuelChanged += (_, f) =>
        {
            Enqueue("ed_fuel_main", f.Main.ToString("F1", Inv));
            Enqueue("ed_fuel_reservoir", f.Reservoir.ToString("F1", Inv));
            Enqueue("ed_fuel_total", (f.Main + f.Reservoir).ToString("F1", Inv));
            if (f.Capacity > 0.01)
                Enqueue("ed_fuel_percent", ((f.Main + f.Reservoir) / f.Capacity * 100).ToString("F0", Inv));
            Enqueue("ed_fuel_capacity", f.Capacity.ToString("F1", Inv));
        };

        // ─── Carico ──────────────────────────────────────────────
        _watcher.CargoChanged += (_, c) =>
        {
            Enqueue("ed_cargo_tons", c.Tons.ToString("F1", Inv));
            if (c.Capacity > 0.01)
                Enqueue("ed_cargo_percent", (c.Tons / c.Capacity * 100).ToString("F0", Inv));
            Enqueue("ed_cargo_capacity", c.Capacity.ToString("F1", Inv));
        };

        // ─── Pips ────────────────────────────────────────────────
        _watcher.PipsChanged += (_, p) =>
        {
            Enqueue("ed_pips_sys", p.Sys.ToString(Inv));
            Enqueue("ed_pips_eng", p.Eng.ToString(Inv));
            Enqueue("ed_pips_wep", p.Wep.ToString(Inv));
        };

        // ─── Gradi e progressi ───────────────────────────────────
        _watcher.RankChanged += (_, r) =>
        {
            Enqueue("ed_rank_combat", r.Combat.ToString(Inv));
            Enqueue("ed_rank_trade", r.Trade.ToString(Inv));
            Enqueue("ed_rank_explore", r.Explore.ToString(Inv));
            Enqueue("ed_rank_empire", r.Empire.ToString(Inv));
            Enqueue("ed_rank_federation", r.Federation.ToString(Inv));
            Enqueue("ed_rank_cqc", r.Cqc.ToString(Inv));
        };
        _watcher.ProgressChanged += (_, p) =>
        {
            Enqueue("ed_progress_combat", p.Combat.ToString(Inv));
            Enqueue("ed_progress_trade", p.Trade.ToString(Inv));
            Enqueue("ed_progress_explore", p.Explore.ToString(Inv));
            Enqueue("ed_progress_empire", p.Empire.ToString(Inv));
            Enqueue("ed_progress_federation", p.Federation.ToString(Inv));
            Enqueue("ed_progress_cqc", p.Cqc.ToString(Inv));
        };

        // ─── Combattimento ───────────────────────────────────────
        _watcher.UnderAttackChanged += (_, v) => Enqueue("ed_combat_under_attack", Bool(v));
        _watcher.InterdictedChanged += (_, v) => Enqueue("ed_combat_interdicted", Bool(v));
        _watcher.EscapedInterdictionChanged += (_, v) => Enqueue("ed_combat_escaped", Bool(v));
        _watcher.BountyChanged += (_, v) => Enqueue("ed_combat_last_bounty", v.ToString(Inv));
        _watcher.HullDamageChanged += (_, v) => Enqueue("ed_combat_hull_damage", v.ToString("F2", Inv));

        // ─── Missioni ────────────────────────────────────────────
        _watcher.MissionCountChanged += (_, v) => Enqueue("ed_mission_count", v.ToString(Inv));
        _watcher.MissionNameChanged += (_, v) => Enqueue("ed_mission_last_name", v);
        _watcher.MissionRewardChanged += (_, v) => Enqueue("ed_mission_last_reward", v.ToString(Inv));

        // ─── Esplorazione ────────────────────────────────────────
        _watcher.ScanBodyChanged += (_, v) => Enqueue("ed_explore_last_scan", v);
        _watcher.ScanTypeChanged += (_, v) => Enqueue("ed_explore_last_scan_type", v);
        _watcher.MaterialChanged += (_, v) => Enqueue("ed_explore_last_material", v);
        _watcher.FssBodiesChanged += (_, v) => Enqueue("ed_explore_fss_bodies", v.ToString(Inv));

        // ─── Mercato ─────────────────────────────────────────────
        _watcher.MarketProfitChanged += (_, v) => Enqueue("ed_market_last_profit", v.ToString(Inv));

        // ─── Comandante ──────────────────────────────────────────
        _watcher.LegalStateChanged += (_, v) => Enqueue("ed_legal_state", v);
        _watcher.BalanceChanged += (_, v) => Enqueue("ed_balance", v.ToString(Inv));

        Actions = [new ResyncAction(this)];
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
    // Azione
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
        public LocalizedText Name => Strings.Actions.Resync.Name();
        public LocalizedText Description => Strings.Actions.Resync.Description();
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
    // Definizioni variabili
    // ─────────────────────────────────────────────────────────────

    private static readonly (string Name, VariableType Type)[] Definitions =
    [
        // Posizione
        ("ed_pos_system",           VariableType.Text),
        ("ed_pos_station",          VariableType.Text),
        ("ed_pos_body",             VariableType.Text),
        ("ed_pos_latitude",         VariableType.Text),
        ("ed_pos_longitude",        VariableType.Text),
        ("ed_pos_altitude",         VariableType.Text),
        ("ed_pos_heading",          VariableType.Text),
        ("ed_pos_starpos_x",        VariableType.Text),
        ("ed_pos_starpos_y",        VariableType.Text),
        ("ed_pos_starpos_z",        VariableType.Text),
        // Nave
        ("ed_ship_type",            VariableType.Text),
        ("ed_ship_name",            VariableType.Text),
        ("ed_ship_ident",           VariableType.Text),
        ("ed_ship_hull_health",     VariableType.Text),
        ("ed_ship_max_jump_range",  VariableType.Text),
        ("ed_ship_rebuy_cost",      VariableType.Text),
        ("ed_ship_module_count",    VariableType.Text),
        // Stato
        ("ed_status_docked",        VariableType.Text),
        ("ed_status_landed",        VariableType.Text),
        ("ed_status_gear_down",     VariableType.Text),
        ("ed_status_shields",       VariableType.Text),
        ("ed_status_supercruise",   VariableType.Text),
        ("ed_status_fa_off",        VariableType.Text),
        ("ed_status_hardpoints",    VariableType.Text),
        ("ed_status_wing",          VariableType.Text),
        ("ed_status_lights",        VariableType.Text),
        ("ed_status_cargo_scoop",   VariableType.Text),
        ("ed_status_silent",        VariableType.Text),
        ("ed_status_scooping",      VariableType.Text),
        ("ed_status_fsd_masslock",  VariableType.Text),
        ("ed_status_fsd_charging",  VariableType.Text),
        ("ed_status_fsd_cooldown",  VariableType.Text),
        ("ed_status_low_fuel",      VariableType.Text),
        ("ed_status_overheat",      VariableType.Text),
        ("ed_status_danger",        VariableType.Text),
        ("ed_status_interdicted",   VariableType.Text),
        ("ed_status_main_ship",     VariableType.Text),
        ("ed_status_fighter",       VariableType.Text),
        ("ed_status_srv",           VariableType.Text),
        ("ed_status_on_foot",       VariableType.Text),
        ("ed_status_in_taxi",       VariableType.Text),
        ("ed_status_multicrew",     VariableType.Text),
        ("ed_status_low_oxygen",    VariableType.Text),
        ("ed_status_low_health",    VariableType.Text),
        // Carburante
        ("ed_fuel_main",            VariableType.Text),
        ("ed_fuel_reservoir",       VariableType.Text),
        ("ed_fuel_total",           VariableType.Text),
        ("ed_fuel_percent",         VariableType.Text),
        ("ed_fuel_capacity",        VariableType.Text),
        // Carico
        ("ed_cargo_tons",           VariableType.Text),
        ("ed_cargo_percent",        VariableType.Text),
        ("ed_cargo_capacity",       VariableType.Text),
        // Pips
        ("ed_pips_sys",             VariableType.Text),
        ("ed_pips_eng",             VariableType.Text),
        ("ed_pips_wep",             VariableType.Text),
        // Gradi
        ("ed_rank_combat",          VariableType.Text),
        ("ed_rank_trade",           VariableType.Text),
        ("ed_rank_explore",         VariableType.Text),
        ("ed_rank_empire",          VariableType.Text),
        ("ed_rank_federation",      VariableType.Text),
        ("ed_rank_cqc",             VariableType.Text),
        ("ed_progress_combat",      VariableType.Text),
        ("ed_progress_trade",       VariableType.Text),
        ("ed_progress_explore",     VariableType.Text),
        ("ed_progress_empire",      VariableType.Text),
        ("ed_progress_federation",  VariableType.Text),
        ("ed_progress_cqc",         VariableType.Text),
        // Combattimento
        ("ed_combat_under_attack",  VariableType.Text),
        ("ed_combat_interdicted",   VariableType.Text),
        ("ed_combat_escaped",       VariableType.Text),
        ("ed_combat_last_bounty",   VariableType.Text),
        ("ed_combat_hull_damage",   VariableType.Text),
        // Missioni
        ("ed_mission_count",        VariableType.Text),
        ("ed_mission_last_name",    VariableType.Text),
        ("ed_mission_last_reward",  VariableType.Text),
        // Esplorazione
        ("ed_explore_last_scan",    VariableType.Text),
        ("ed_explore_last_scan_type", VariableType.Text),
        ("ed_explore_last_material", VariableType.Text),
        ("ed_explore_fss_bodies",   VariableType.Text),
        // Mercato
        ("ed_market_last_profit",   VariableType.Text),
        // Comandante
        ("ed_commander_name",       VariableType.Text),
        ("ed_commander_credits",    VariableType.Text),
        ("ed_legal_state",          VariableType.Text),
        ("ed_balance",              VariableType.Text),
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
                    pending[w.Name] = w.Value;

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
    // Push iniziale
    // ─────────────────────────────────────────────────────────────

    private async Task PushAllAsync()
    {
        var ctx = _context;
        if (ctx == null) return;

        var s = _watcher.Status;
        var list = new List<(string Name, string Value)>
        {
            ("ed_pos_system", s.System),
            ("ed_pos_station", s.Station),
            ("ed_pos_body", s.Body),
            ("ed_pos_latitude", s.Latitude.ToString("F6", Inv)),
            ("ed_pos_longitude", s.Longitude.ToString("F6", Inv)),
            ("ed_pos_altitude", s.Altitude.ToString("F1", Inv)),
            ("ed_pos_heading", s.Heading.ToString(Inv)),
            ("ed_pos_starpos_x", s.StarPosX.ToString("F3", Inv)),
            ("ed_pos_starpos_y", s.StarPosY.ToString("F3", Inv)),
            ("ed_pos_starpos_z", s.StarPosZ.ToString("F3", Inv)),
            ("ed_ship_type", s.ShipType),
            ("ed_ship_name", s.ShipName),
            ("ed_ship_ident", s.ShipIdent),
            ("ed_ship_hull_health", s.HullHealth.ToString("F2", Inv)),
            ("ed_ship_max_jump_range", s.MaxJumpRange.ToString("F1", Inv)),
            ("ed_ship_rebuy_cost", s.RebuyCost.ToString(Inv)),
            ("ed_ship_module_count", s.ModuleCount.ToString(Inv)),
            ("ed_status_docked", Bool(s.IsDocked)),
            ("ed_status_landed", Bool(s.IsLanded)),
            ("ed_status_gear_down", Bool(s.LandingGearDown)),
            ("ed_status_shields", Bool(s.ShieldsUp)),
            ("ed_status_supercruise", Bool(s.Supercruise)),
            ("ed_status_fa_off", Bool(s.FlightAssistOff)),
            ("ed_status_hardpoints", Bool(s.HardpointsDeployed)),
            ("ed_status_wing", Bool(s.InWing)),
            ("ed_status_lights", Bool(s.LightsOn)),
            ("ed_status_cargo_scoop", Bool(s.CargoScoopDeployed)),
            ("ed_status_silent", Bool(s.SilentRunning)),
            ("ed_status_scooping", Bool(s.ScoopingFuel)),
            ("ed_status_fsd_masslock", Bool(s.FsdMassLocked)),
            ("ed_status_fsd_charging", Bool(s.FsdCharging)),
            ("ed_status_fsd_cooldown", Bool(s.FsdCooldown)),
            ("ed_status_low_fuel", Bool(s.LowFuel)),
            ("ed_status_overheat", Bool(s.Overheating)),
            ("ed_status_danger", Bool(s.InDanger)),
            ("ed_status_interdicted", Bool(s.BeingInterdicted)),
            ("ed_status_main_ship", Bool(s.InMainShip)),
            ("ed_status_fighter", Bool(s.InFighter)),
            ("ed_status_srv", Bool(s.InSrv)),
            ("ed_status_on_foot", Bool(s.OnFoot)),
            ("ed_status_in_taxi", Bool(s.InTaxi)),
            ("ed_status_multicrew", Bool(s.InMulticrew)),
            ("ed_status_low_oxygen", Bool(s.LowOxygen)),
            ("ed_status_low_health", Bool(s.LowHealth)),
            ("ed_fuel_main", s.FuelMain.ToString("F1", Inv)),
            ("ed_fuel_reservoir", s.FuelReservoir.ToString("F1", Inv)),
            ("ed_fuel_total", s.TotalFuel.ToString("F1", Inv)),
            ("ed_fuel_percent", s.FuelPercent.ToString("F0", Inv)),
            ("ed_fuel_capacity", s.FuelCapacity.ToString("F1", Inv)),
            ("ed_cargo_tons", s.CargoTons.ToString("F1", Inv)),
            ("ed_cargo_percent", s.CargoPercent.ToString("F0", Inv)),
            ("ed_cargo_capacity", s.CargoCapacity.ToString("F1", Inv)),
            ("ed_pips_sys", s.PipsSys.ToString(Inv)),
            ("ed_pips_eng", s.PipsEng.ToString(Inv)),
            ("ed_pips_wep", s.PipsWep.ToString(Inv)),
            ("ed_rank_combat", s.RankCombat.ToString(Inv)),
            ("ed_rank_trade", s.RankTrade.ToString(Inv)),
            ("ed_rank_explore", s.RankExplore.ToString(Inv)),
            ("ed_rank_empire", s.RankEmpire.ToString(Inv)),
            ("ed_rank_federation", s.RankFederation.ToString(Inv)),
            ("ed_rank_cqc", s.RankCqc.ToString(Inv)),
            ("ed_progress_combat", s.ProgressCombat.ToString(Inv)),
            ("ed_progress_trade", s.ProgressTrade.ToString(Inv)),
            ("ed_progress_explore", s.ProgressExplore.ToString(Inv)),
            ("ed_progress_empire", s.ProgressEmpire.ToString(Inv)),
            ("ed_progress_federation", s.ProgressFederation.ToString(Inv)),
            ("ed_progress_cqc", s.ProgressCqc.ToString(Inv)),
            ("ed_combat_under_attack", Bool(s.UnderAttack)),
            ("ed_combat_interdicted", Bool(s.Interdicted)),
            ("ed_combat_escaped", Bool(s.EscapedInterdiction)),
            ("ed_combat_last_bounty", s.LastBounty.ToString(Inv)),
            ("ed_combat_hull_damage", s.HullDamage.ToString("F2", Inv)),
            ("ed_mission_count", s.MissionCount.ToString(Inv)),
            ("ed_mission_last_name", s.LastMissionName),
            ("ed_mission_last_reward", s.LastMissionReward.ToString(Inv)),
            ("ed_explore_last_scan", s.LastScanBody),
            ("ed_explore_last_scan_type", s.LastScanType),
            ("ed_explore_last_material", s.LastMaterial),
            ("ed_explore_fss_bodies", s.FssBodies.ToString(Inv)),
            ("ed_market_last_profit", s.LastMarketProfit.ToString(Inv)),
            ("ed_commander_name", s.Commander),
            ("ed_commander_credits", s.Credits.ToString(Inv)),
            ("ed_legal_state", s.LegalState),
            ("ed_balance", s.Balance.ToString(Inv)),
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