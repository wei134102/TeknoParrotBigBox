using System.ComponentModel;
using System.Windows;

namespace TeknoParrotBigBox
{
    public partial class SettingsWindow : Window, INotifyPropertyChanged
    {
        private string _windowTitle;
        private string _languageLabel;
        private string _buttonOkText;
        private string _buttonCancelText;
        private int _languageIndex;

        public string ButtonOkText { get => _buttonOkText; set { _buttonOkText = value; OnPropertyChanged(nameof(ButtonOkText)); } }
        public string ButtonCancelText { get => _buttonCancelText; set { _buttonCancelText = value; OnPropertyChanged(nameof(ButtonCancelText)); } }

        public string WindowTitle
        {
            get => _windowTitle;
            set { _windowTitle = value; OnPropertyChanged(nameof(WindowTitle)); }
        }

        public string LanguageLabel
        {
            get => _languageLabel;
            set { _languageLabel = value; OnPropertyChanged(nameof(LanguageLabel)); }
        }

        public int LanguageIndex
        {
            get => _languageIndex;
            set { _languageIndex = value; OnPropertyChanged(nameof(LanguageIndex)); }
        }

        public SettingsWindow()
        {
            InitializeComponent();
            DataContext = this;
            WindowTitle = Localization.Get("SettingsTitle");
            LanguageLabel = Localization.Get("SettingsLanguageLabel");
            ComboLanguage.Items.Clear();
            ComboLanguage.Items.Add(Localization.Get("SettingsLangZh"));
            ComboLanguage.Items.Add(Localization.Get("SettingsLangEn"));
            LanguageIndex = Localization.IsEnglish ? 1 : 0;
            ButtonOkText = Localization.Get("ButtonOk");
            ButtonCancelText = Localization.Get("ButtonCancel");
        }

        private void ButtonOk_Click(object sender, RoutedEventArgs e)
        {
            Localization.Language = LanguageIndex == 1 ? Localization.LangEn : Localization.LangZh;
            DialogResult = true;
            Close();
        }

        private void ButtonCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
