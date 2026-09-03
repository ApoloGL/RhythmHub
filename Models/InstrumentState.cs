namespace RhythmHub.Models;

// Normalized state of any rhythm instrument
public class InstrumentState
{
    public bool Green { get; set; }
    public bool Red { get; set; }
    public bool Yellow { get; set; }
    public bool Blue { get; set; }
    public bool Orange { get; set; }
    public bool White3 { get; set; }

    public bool StrumUp { get; set; }
    public bool StrumDown { get; set; }
    public bool DpadUp { get; set; }
    public bool DpadDown { get; set; }
    public bool DpadLeft { get; set; }
    public bool DpadRight { get; set; }
    
    public bool HeroPower { get; set; } // Tilt or button
    public bool Start { get; set; }
    public bool Select { get; set; }
    
    public float Whammy { get; set; }
    public float Tilt { get; set; }

    /// <summary>
    /// Resets all inputs and axes to rest state without heap allocations.
    /// </summary>
    public void Reset()
    {
        Green = false;
        Red = false;
        Yellow = false;
        Blue = false;
        Orange = false;
        White3 = false;
        StrumUp = false;
        StrumDown = false;
        DpadUp = false;
        DpadDown = false;
        DpadLeft = false;
        DpadRight = false;
        HeroPower = false;
        Start = false;
        Select = false;
        Whammy = 0.0f;
        Tilt = 0.0f;
    }

    /// <summary>
    /// Copies state from another instance without heap allocation.
    /// </summary>
    public void CopyFrom(InstrumentState source)
    {
        Green = source.Green;
        Red = source.Red;
        Yellow = source.Yellow;
        Blue = source.Blue;
        Orange = source.Orange;
        White3 = source.White3;
        StrumUp = source.StrumUp;
        StrumDown = source.StrumDown;
        DpadUp = source.DpadUp;
        DpadDown = source.DpadDown;
        DpadLeft = source.DpadLeft;
        DpadRight = source.DpadRight;
        HeroPower = source.HeroPower;
        Start = source.Start;
        Select = source.Select;
        Whammy = source.Whammy;
        Tilt = source.Tilt;
    }
}
