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
}
