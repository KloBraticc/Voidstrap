using System.ComponentModel;

namespace Voidstrap.Models
{
    public class DMKeybind : INotifyPropertyChanged
    {
        private string _key = "";
        private string _button = "";

        public string Key
        {
            get => _key;
            set
            {
                if (_key == value) return;
                _key = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Key)));
            }
        }

        public string Button
        {
            get => _button;
            set
            {
                if (_button == value) return;
                _button = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Button)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
