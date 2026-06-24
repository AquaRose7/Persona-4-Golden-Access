using p4g64.accessibility.Template.Configuration;
using System.ComponentModel;

namespace p4g64.accessibility.Configuration;
public class Config : Configurable<Config>
{
    [DisplayName("Announce Dialogue")]
    [Description("Reads dialogue lines aloud via screen reader.")]
    [DefaultValue(true)]
    public bool AnnounceDialogue { get; set; } = true;

    [DisplayName("Announce Field Location")]
    [Description("Announces the area name when moving between locations.")]
    [DefaultValue(true)]
    public bool AnnounceFieldLocation { get; set; } = true;

    [DisplayName("Announce Battle")]
    [Description("Announces battle menu items and skills.")]
    [DefaultValue(true)]
    public bool AnnounceBattle { get; set; } = true;

    [DisplayName("Announce Save Slots")]
    [Description("Reads save slot details when navigating the load/save screen.")]
    [DefaultValue(true)]
    public bool AnnounceSaveSlots { get; set; } = true;

    [DisplayName("Debug Mode")]
    [Description("Logs additional information to the console that is useful for debugging.")]
    [DefaultValue(false)]
    public bool DebugEnabled { get; set; } = false;
}

/// <summary>
/// Allows you to override certain aspects of the configuration creation process (e.g. create multiple configurations).
/// Override elements in <see cref="ConfiguratorMixinBase"/> for finer control.
/// </summary>
public class ConfiguratorMixin : ConfiguratorMixinBase
{
    //
}
