using System.Collections.ObjectModel;
using System.ComponentModel;
using WebHealthCheck.Models;

namespace WebHealthCheck.ViewModels
{
    public class AccessibilityResultViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public void RaisePropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ShowDataViewModel : AccessibilityResultViewModel
    {
        //数据源
        ObservableCollection<AccessibilityResult> _accessibilityResults = [];
        public ObservableCollection<AccessibilityResult> AccessibilityResults
        {

            get { return _accessibilityResults; }
            set
            {
                _accessibilityResults = value;
                RaisePropertyChanged(nameof(AccessibilityResults));
            }
        }

        public ShowDataViewModel()
        {
            //AccessibilityResults.Add(new AccessibilityResult() { Id = 1, Target = "www.baidu.com", Url = "https://www.baidu.com", AccessState = "3/3", WebTitle = "百度一下，你就知道" });
        }
    }
}
