namespace EliteDangerousPlugin;

public sealed class EliteStatusModel
{
    // Da Status.json (Flags sono uint32)
    public uint Flags { get; set; }
    public uint Flags2 { get; set; }
    public double FuelMain { get; set; }
    public double FuelReservoir { get; set; }
    public double FuelCapacity { get; set; }   // da Loadout
    public string LegalState { get; set; } = "Unknown";
    public string BodyName { get; set; } = "";
    public long Balance { get; set; }
    public double Cargo { get; set; }

    // Pips (0-8 per SYS, ENG, WEP)
    public int PipsSys { get; set; }
    public int PipsEng { get; set; }
    public int PipsWep { get; set; }

    // Dai journal (FSDJump / Location)
    public string CurrentSystem { get; set; } = "Unknown";
    public string CurrentStation { get; set; } = "";

    // Da LoadGame / Loadout
    public string Commander { get; set; } = "";
    public string ShipType { get; set; } = "";
    public string ShipName { get; set; } = "";

    // Comodità
    public double TotalFuel => FuelMain + FuelReservoir;

    public double FuelPercent =>
        FuelCapacity > 0.01 ? (TotalFuel / FuelCapacity) * 100.0 : 0.0;

    // ─── Flag esistenti ─────────────────────────────────────────
    public bool IsDocked => (Flags & 1u) != 0;
    public bool IsLanded => (Flags & (1u << 1)) != 0;
    public bool LandingGearDown => (Flags & (1u << 2)) != 0;
    public bool ShieldsUp => (Flags & (1u << 3)) != 0;
    public bool Supercruise => (Flags & (1u << 4)) != 0;
    public bool FlightAssistOff => (Flags & (1u << 5)) != 0;
    public bool HardpointsDeployed => (Flags & (1u << 6)) != 0;
    public bool FsdCharging => (Flags & (1u << 17)) != 0;
    public bool FsdCooldown => (Flags & (1u << 18)) != 0;
    public bool LowFuel => (Flags & (1u << 19)) != 0;

    // ─── Nuove 12 flag ──────────────────────────────────────────
    public bool InWing => (Flags & (1u << 7)) != 0;
    public bool LightsOn => (Flags & (1u << 8)) != 0;
    public bool CargoScoopDeployed => (Flags & (1u << 9)) != 0;
    public bool SilentRunning => (Flags & (1u << 10)) != 0;
    public bool ScoopingFuel => (Flags & (1u << 11)) != 0;
    public bool FsdMassLocked => (Flags & (1u << 16)) != 0;
    public bool Overheating => (Flags & (1u << 20)) != 0;
    public bool InDanger => (Flags & (1u << 22)) != 0;
    public bool BeingInterdicted => (Flags & (1u << 23)) != 0;
    public bool InMainShip => (Flags & (1u << 24)) != 0;
    public bool InFighter => (Flags & (1u << 25)) != 0;
    public bool InSrv => (Flags & (1u << 26)) != 0;

    // ─── Reset: azzera tutto quando il gioco si chiude ──────────
    public void Reset()
    {
        Flags = 0;
        Flags2 = 0;
        FuelMain = 0;
        FuelReservoir = 0;
        FuelCapacity = 0;
        LegalState = "Unknown";
        BodyName = "";
        Balance = 0;
        Cargo = 0;
        PipsSys = 0;
        PipsEng = 0;
        PipsWep = 0;
        CurrentStation = "";
    }
}