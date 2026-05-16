using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using System.Xml.Serialization;
using GI_Subtitles.Common;
using GI_Subtitles.Models;

namespace GI_Subtitles.Core.Input
{
    /// <summary>
    /// Hotkey settings configuration
    /// </summary>
    [XmlRoot("HotkeySettings")]
    public class HotkeySettings
    {
        [XmlArray("Hotkeys")]
        [XmlArrayItem("Hotkey")]
        public List<HotkeyData> Hotkeys { get; set; } = new List<HotkeyData>();
    }

    /// <summary>
    /// Hotkey settings manager
    /// </summary>
    public static class HotkeySettingsManager
    {
        private static string _settingsPath = Path.Combine(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kaption"), "hotkeySettings.xml");

        public static HotkeySettings LoadSettings()
        {
            if (!File.Exists(_settingsPath))
            {
                // Return default settings
                return GetDefaultSettings();
            }

            try
            {
                using (var reader = new StreamReader(_settingsPath))
                {
                    var serializer = new XmlSerializer(typeof(HotkeySettings));
                    return (HotkeySettings)serializer.Deserialize(reader);
                }
            }
            catch
            {
                // Return default settings when reading fails
                return GetDefaultSettings();
            }
        }

        public static void SaveSettings(HotkeySettings settings)
        {
            try
            {
                using (var writer = new StreamWriter(_settingsPath))
                {
                    var serializer = new XmlSerializer(typeof(HotkeySettings));
                    serializer.Serialize(writer, settings);
                }
            }
            catch (Exception ex) { Logger.Log.Error($"Failed to save hotkey settings: {ex.Message}"); }
        }

        private static string GetLocalizedString(string resourceKey, string fallback)
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app != null)
                {
                    var value = app.TryFindResource(resourceKey) as string;
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }
            catch
            {
                // ignore and fallback
            }
            return fallback;
        }

        private static HotkeySettings GetDefaultSettings()
        {
            return new HotkeySettings
            {
                Hotkeys = new List<HotkeyData>
                {
                    new HotkeyData
                    {
                        Id = 9000, IsCtrl = true, IsShift = true, SelectedKey = 'S',
                        Description = GetLocalizedString("Hotkey_9000_Description", "开始/停止识别字幕")
                    },
                    new HotkeyData
                    {
                        Id = 9001, IsCtrl = true, IsShift = true, SelectedKey = 'R',
                        Description = GetLocalizedString("Hotkey_9001_Description", "选择字幕区域（第一行）")
                    },
                    new HotkeyData
                    {
                        Id = 9002, IsCtrl = true, IsShift = true, SelectedKey = 'H',
                        Description = GetLocalizedString("Hotkey_9002_Description", "隐藏双语字幕")
                    },
                    new HotkeyData
                    {
                        Id = 9003, IsCtrl = true, IsShift = true, SelectedKey = 'D',
                        Description = GetLocalizedString("Hotkey_9003_Description", "展示识别区域")
                    },
                    new HotkeyData
                    {
                        // Force-OCR hotkey default changed from bare backtick to
                        // Ctrl+1 in session 26 — a lot of keyboards put backtick
                        // in an awkward spot (top-left above Tab) and some
                        // non-US layouts put it behind a dead-key chord, making
                        // the "hit it fast during typewriter animation" workflow
                        // painful. Ctrl+1 is reachable from the WASD home row.
                        Id = 9004, IsCtrl = true, IsShift = false, SelectedKey = '1',
                        Description = GetLocalizedString("Hotkey_9004_Description", "立即识别翻译")
                    },
                    new HotkeyData
                    {
                        Id = 9005, IsCtrl = true, IsShift = false, SelectedKey = 'Q',
                        Description = GetLocalizedString("Hotkey_9005_Description", "Quick translate selected area")
                    }
                }
            };
        }
    }

    /// <summary>
    /// Hotkey view model for UI binding
    /// </summary>
    public class HotkeyViewModel : INotifyPropertyChanged
    {
        private int _id;
        private string _description;
        private bool _isCtrl;
        private bool _isShift;
        private bool _isAlt;
        private char _selectedKey;
        private bool _isEditing;
        private List<char> _availableKeys;

        public event PropertyChangedEventHandler PropertyChanged;

        public int Id
        {
            get => _id;
            set
            {
                _id = value;
                OnPropertyChanged(nameof(Id));
            }
        }

        public string Description
        {
            get => _description;
            set
            {
                _description = value;
                OnPropertyChanged(nameof(Description));
            }
        }

        /// <summary>
        /// Longer "when is this useful?" copy shown in the info-icon tooltip
        /// next to each hotkey row. Set from localized resources by the
        /// SettingsWindow loader.
        /// </summary>
        private string _infoText;
        public string InfoText
        {
            get => _infoText;
            set { _infoText = value; OnPropertyChanged(nameof(InfoText)); }
        }

        /// <summary>
        /// Splits the hotkey list into primary vs. advanced shortcuts. The
        /// XAML binds a CollectionViewSource to this property so both groups
        /// render in a single ListView with distinct headers. Power-user
        /// shortcuts (e.g. force re-translate, hide subtitles) live in the
        /// Advanced group so the default surface stays short.
        /// </summary>
        private bool _isAdvanced;
        public bool IsAdvanced
        {
            get => _isAdvanced;
            set { _isAdvanced = value; OnPropertyChanged(nameof(IsAdvanced)); }
        }

        public bool IsCtrl
        {
            get => _isCtrl;
            set
            {
                _isCtrl = value;
                OnPropertyChanged(nameof(IsCtrl));
                OnPropertyChanged(nameof(GetHotkeyText));
                OnPropertyChanged(nameof(DisplayText));
            }
        }

        public bool IsShift
        {
            get => _isShift;
            set
            {
                _isShift = value;
                OnPropertyChanged(nameof(IsShift));
                OnPropertyChanged(nameof(GetHotkeyText));
                OnPropertyChanged(nameof(DisplayText));
            }
        }

        public bool IsAlt
        {
            get => _isAlt;
            set
            {
                _isAlt = value;
                OnPropertyChanged(nameof(IsAlt));
                OnPropertyChanged(nameof(GetHotkeyText));
                OnPropertyChanged(nameof(DisplayText));
            }
        }

        public char SelectedKey
        {
            get => _selectedKey;
            set
            {
                _selectedKey = value;
                OnPropertyChanged(nameof(SelectedKey));
                OnPropertyChanged(nameof(GetHotkeyText));
                OnPropertyChanged(nameof(DisplayText));
            }
        }

        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                _isEditing = value;
                OnPropertyChanged(nameof(IsEditing));
                OnPropertyChanged(nameof(ButtonText));
                OnPropertyChanged(nameof(DisplayText));
            }
        }

        /// <summary>
        /// User-facing label shown on the hotkey chip button. Flips to a
        /// "Press a shortcut…" prompt while we're in recording mode so the
        /// end user knows the chip is live-listening.
        /// </summary>
        public string DisplayText
        {
            get
            {
                if (IsEditing)
                {
                    try
                    {
                        var prompt = System.Windows.Application.Current?
                            .TryFindResource("Hotkey_Recording_Prompt") as string;
                        if (!string.IsNullOrEmpty(prompt)) return prompt;
                    }
                    catch { /* fallback below */ }
                    return "Press a shortcut…";
                }
                var parts = new List<string>();
                if (IsCtrl) parts.Add("Ctrl");
                if (IsShift) parts.Add("Shift");
                if (IsAlt) parts.Add("Alt");
                if (SelectedKey != '\0') parts.Add(SelectedKey.ToString());
                return string.Join(" + ", parts);
            }
        }

        public List<char> AvailableKeys
        {
            get => _availableKeys;
            set
            {
                _availableKeys = value;
                OnPropertyChanged(nameof(AvailableKeys));
            }
        }

        public string ButtonText => IsEditing ? "Cancel" : "Edit";

        public ICommand ToggleEditCommand => new RelayCommand(ToggleEdit);

        public string GetHotkeyText()
        {
            var parts = new List<string>();
            if (IsCtrl) parts.Add("Ctrl");
            if (IsShift) parts.Add("Shift");
            if (IsAlt) parts.Add("Alt");
            parts.Add(SelectedKey.ToString());
            return string.Join("+", parts);
        }

        private void ToggleEdit()
        {
            IsEditing = !IsEditing;
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

