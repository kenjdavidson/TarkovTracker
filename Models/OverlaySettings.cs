using System;

public class OverlaySettings
{
    public double OverlayLeft { get; set; } = 0;

    public double OverlayTop { get; set; } = 0;

    /// <summary>
    /// Height of the overlay window.
    /// </summary>
    public double OverlayHeight { get; set; } = 600;

    /// <summary>
    /// Width of the overlay window.
    /// </summary>
    public double OverlayWidth { get; set; } = 800;    
    
    /// <summary>
    /// Default overlay map opacity (20-100). Applied when the overlay window opens.
    /// </summary>
    public double OverlayDefaultOpacityPercent { get; set; } = 80;
}