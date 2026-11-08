using Artemis.Core;

namespace Artemis.Plugins.LayerBrushes.Chroma.LayerBrushes.PropertyGroups;

#pragma warning disable CS8618
public class ChromaPropertyGroup : LayerPropertyGroup
{
    [PropertyDescription(Description = "Colors unmapped LEDs with the default color")]
    public BoolLayerProperty UseDefaultLed { get; set; }

    [PropertyDescription(Description = "Colors unmapped LEDs with one row of the keyboard grid, spread from left to right within each device (for LED strips such as speakers). Takes priority over the default color")]
    public BoolLayerProperty SpreadKeyboardRow { get; set; }

    [PropertyDescription(Description = "Keyboard grid row to spread (1 = function key row, 6 = space bar row)", MinInputValue = 1, MaxInputValue = 6)]
    public IntLayerProperty KeyboardRow { get; set; }

    [PropertyDescription(Description = "Turns black LEDs transparent")]
    public BoolLayerProperty TransparentBlack { get; set; }

    [PropertyDescription(Description = "Enhances colors to be more vibrant")]
    public BoolLayerProperty OverwatchEnhanceColors { get; set; }

    protected override void PopulateDefaults()
    {
        UseDefaultLed.DefaultValue = true;
        SpreadKeyboardRow.DefaultValue = false;
        // Row 1 also holds the logo and row 3-5 the keys games tend to highlight, which show up as fixed spots.
        KeyboardRow.DefaultValue = 2;
        TransparentBlack.DefaultValue = false;
        OverwatchEnhanceColors.DefaultValue = false;
    }

    protected override void EnableProperties()
    {
    }

    protected override void DisableProperties()
    {
    }
}