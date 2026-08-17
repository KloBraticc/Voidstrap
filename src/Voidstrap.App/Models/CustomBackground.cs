using System.ComponentModel;

namespace Voidstrap.Models
{
    public class CustomBackground : INotifyPropertyChanged
    {
        private string _name = "";

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Name"));
                }
            }
        }

        public string FolderPath { get; set; } = "";
        public string FilePath { get; set; } = "";

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
