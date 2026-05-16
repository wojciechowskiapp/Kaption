namespace GI_Subtitles.Models
{
    /// <summary>
    /// Hotkey data model
    /// </summary>
    public class HotkeyData
    {
        public int Id { get; set; }
        public bool IsCtrl { get; set; }
        public bool IsShift { get; set; }
        // New in session 31. Older hotkeySettings.xml files don't have this
        // element — XmlSerializer defaults it to false, so upgrades are
        // automatic and no migration is required.
        public bool IsAlt { get; set; }
        public char SelectedKey { get; set; }
        public string Description { get; set; }
    }
}

