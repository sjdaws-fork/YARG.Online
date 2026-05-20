namespace YARG.Online.Game.Contracts.Enums;

// Wire-format mirror of YARG.Core.Instrument. Values MUST match the source enum
// (which is replay-serialized and treated as stable).
public enum InstrumentId : byte
{
    FiveFretGuitar = 0,
    FiveFretBass = 1,
    FiveFretRhythm = 2,
    FiveFretCoopGuitar = 3,
    Keys = 4,

    SixFretGuitar = 10,
    SixFretBass = 11,
    SixFretRhythm = 12,
    SixFretCoopGuitar = 13,

    FourLaneDrums = 20,
    ProDrums = 21,
    FiveLaneDrums = 22,
    EliteDrums = 23,

    ProGuitar_17Fret = 30,
    ProGuitar_22Fret = 31,
    ProBass_17Fret = 32,
    ProBass_22Fret = 33,

    ProKeys = 34,

    Vocals = 40,
    Harmony = 41,

    Band = byte.MaxValue,
}
