namespace EliteDangerousPlugin;

public sealed class EliteStatusModel
{
    // ─── Posizione ──────────────────────────────────────────────
    public string System { get; set; } = "Unknown";
    public string Station { get; set; } = "";
    public string Body { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }
    public int Heading { get; set; }
    public double StarPosX { get; set; }
    public double StarPosY { get; set; }
    public double StarPosZ { get; set; }

    // ─── Nave ───────────────────────────────────────────────────
    public string ShipType { get; set; } = "";
    public string ShipName { get; set; } = "";
    public string ShipIdent { get; set; } = "";
    public double HullHealth { get; set; }
    public double MaxJumpRange { get; set; }
    public long RebuyCost { get; set; }
    public int ModuleCount { get; set; }

    // ─── Stato (Status.json) ────────────────────────────────────
    public uint Flags { get; set; }
    public uint Flags2 { get; set; }

    // ─── Carburante ─────────────────────────────────────────────
    public double FuelMain { get; set; }
    public double FuelReservoir { get; set; }
    public double FuelCapacity { get; set; }

    // ─── Carico ─────────────────────────────────────────────────
    public double CargoTons { get; set; }
    public double CargoCapacity { get; set; }

    // ─── Pips ───────────────────────────────────────────────────
    public int PipsSys { get; set; }
    public int PipsEng { get; set; }
    public int PipsWep { get; set; }

    // ─── Gradi ──────────────────────────────────────────────────
    public int RankCombat { get; set; }
    public int RankTrade { get; set; }
    public int RankExplore { get; set; }
    public int RankEmpire { get; set; }
    public int RankFederation { get; set; }
    public int RankCqc { get; set; }
    public int ProgressCombat { get; set; }
    public int ProgressTrade { get; set; }
    public int ProgressExplore { get; set; }
    public int ProgressEmpire { get; set; }
    public int ProgressFederation { get; set; }
    public int ProgressCqc { get; set; }

    // ─── Combattimento ──────────────────────────────────────────
    public bool UnderAttack { get; set; }
    public bool Interdicted { get; set; }
    public bool EscapedInterdiction { get; set; }
    public long LastBounty { get; set; }
    public double HullDamage { get; set; }

    // ─── Missioni ───────────────────────────────────────────────
    public int MissionCount { get; set; }
    public string LastMissionName { get; set; } = "";
    public long LastMissionReward { get; set; }

    // ─── Esplorazione ───────────────────────────────────────────
    public string LastScanBody { get; set; } = "";
    public string LastScanType { get; set; } = "";
    public string LastMaterial { get; set; } = "";
    public int FssBodies { get; set; }

    // ─── Mercato ────────────────────────────────────────────────
    public long LastMarketProfit { get; set; }

    // ─── Comandante ─────────────────────────────────────────────
    public string Commander { get; set; } = "";
    public long Credits { get; set; }
    public string LegalState { get; set; } = "Unknown";
    public long Balance { get; set; }

    // ─── Comodità ───────────────────────────────────────────────
    public double TotalFuel => FuelMain + FuelReservoir;
    public double FuelPercent => FuelCapacity > 0.01 ? (TotalFuel / FuelCapacity) * 100.0 : 0.0;
    public double CargoPercent => CargoCapacity > 0.01 ? (CargoTons / CargoCapacity) * 100.0 : 0.0;

    // ─── Flag da Status.json ────────────────────────────────────
    public bool IsDocked => (Flags & 1u) != 0;
    public bool IsLanded => (Flags & (1u << 1)) != 0;
    public bool LandingGearDown => (Flags & (1u << 2)) != 0;
    public bool ShieldsUp => (Flags & (1u << 3)) != 0;
    public bool Supercruise => (Flags & (1u << 4)) != 0;
    public bool FlightAssistOff => (Flags & (1u << 5)) != 0;
    public bool HardpointsDeployed => (Flags & (1u << 6)) != 0;
    public bool InWing => (Flags & (1u << 7)) != 0;
    public bool LightsOn => (Flags & (1u << 8)) != 0;
    public bool CargoScoopDeployed => (Flags & (1u << 9)) != 0;
    public bool SilentRunning => (Flags & (1u << 10)) != 0;
    public bool ScoopingFuel => (Flags & (1u << 11)) != 0;
    public bool FsdMassLocked => (Flags & (1u << 16)) != 0;
    public bool FsdCharging => (Flags & (1u << 17)) != 0;
    public bool FsdCooldown => (Flags & (1u << 18)) != 0;
    public bool LowFuel => (Flags & (1u << 19)) != 0;
    public bool Overheating => (Flags & (1u << 20)) != 0;
    public bool InDanger => (Flags & (1u << 22)) != 0;
    public bool BeingInterdicted => (Flags & (1u << 23)) != 0;
    public bool InMainShip => (Flags & (1u << 24)) != 0;
    public bool InFighter => (Flags & (1u << 25)) != 0;
    public bool InSrv => (Flags & (1u << 26)) != 0;

    // ─── Flag da Flags2 (Odyssey) ───────────────────────────────
    public bool OnFoot => (Flags2 & 1u) != 0;
    public bool InTaxi => (Flags2 & (1u << 1)) != 0;
    public bool InMulticrew => (Flags2 & (1u << 2)) != 0;
    public bool LowOxygen => (Flags2 & (1u << 6)) != 0;
    public bool LowHealth => (Flags2 & (1u << 7)) != 0;

    public void Reset()
    {
        System = "Unknown"; Station = ""; Body = "";
        Latitude = 0; Longitude = 0; Altitude = 0; Heading = 0;
        StarPosX = 0; StarPosY = 0; StarPosZ = 0;
        ShipType = ""; ShipName = ""; ShipIdent = "";
        HullHealth = 0; MaxJumpRange = 0; RebuyCost = 0; ModuleCount = 0;
        Flags = 0; Flags2 = 0;
        FuelMain = 0; FuelReservoir = 0; FuelCapacity = 0;
        CargoTons = 0; CargoCapacity = 0;
        PipsSys = 0; PipsEng = 0; PipsWep = 0;
        RankCombat = 0; RankTrade = 0; RankExplore = 0;
        RankEmpire = 0; RankFederation = 0; RankCqc = 0;
        ProgressCombat = 0; ProgressTrade = 0; ProgressExplore = 0;
        ProgressEmpire = 0; ProgressFederation = 0; ProgressCqc = 0;
        UnderAttack = false; Interdicted = false; EscapedInterdiction = false;
        LastBounty = 0; HullDamage = 0;
        MissionCount = 0; LastMissionName = ""; LastMissionReward = 0;
        LastScanBody = ""; LastScanType = ""; LastMaterial = ""; FssBodies = 0;
        LastMarketProfit = 0;
        Commander = ""; Credits = 0; LegalState = "Unknown"; Balance = 0;
    }
}