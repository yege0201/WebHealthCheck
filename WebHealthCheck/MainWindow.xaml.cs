using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WebHealthCheck.ViewModels;

using HtmlAgilityPack;
using System.Diagnostics;
using System.Net.Http;
using System.Globalization;
using System.Windows.Data;
using System.Text;

namespace WebHealthCheck
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private ShowDataViewModel _showDataViewModel;
        private BackgroundWorker _checkHealthWorker;

        public MainWindow()
        {
            InitializeComponent();

            InitCheckHealthBackgroundWorker();

            DataContext = _showDataViewModel = new ShowDataViewModel();
        }

        private void ClearTargetsButton_Click(object sender, RoutedEventArgs e)
        {
            TargetsBox.Text = "";
            MessageBox.Show("目标列表已清空");
        }

        private async void ImportFromFileButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*" // 设置文件筛选器，这里设置为显示所有文件
            };

            if (openFileDialog.ShowDialog() == true)
            {
                // 用户选择了文件，可以通过 openFileDialog.FileName 获取文件路径
                var selectedFileName = openFileDialog.FileName;
                var fileExtension = System.IO.Path.GetExtension(selectedFileName).ToLower();

                switch (fileExtension)
                {
                    case ".txt":
                        var targets = await LoadTargetsFromTextFileAsync(selectedFileName);
                        TargetsBox.Text = string.Join("\n", targets);
                        MessageBox.Show("导入目标列表成功");
                        break;
                    default:
                        MessageBox.Show("暂时不支持此类型文件");
                        break;
                }
            }
        }

        private async Task<string> ReadTextFileAsync(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }

        private async Task<string> LoadTargetsFromTextFileAsync(string filePath)
        {
            var fileContent = await ReadTextFileAsync(filePath);
            return fileContent;
        }

        private void ProcessTargets()
        {
            var targets = new List<string>();
            var validCount = 0;
            var emptyCount = 0;

            var targetArray = TargetsBox.Text.Split("\n");

            foreach (var target in targetArray)
            {
                if (string.IsNullOrEmpty(target))
                {
                    emptyCount++;
                    continue;
                }
                targets.Add(target);
                validCount++;
            }

            TotalCount.Text = validCount.ToString();

            MessageBox.Show($"获取到有效目标 {validCount} 个，空行 {emptyCount} 个。");
        }

        private string GetHttpMethod()
        {
            if ((bool)MethodGetRadioButton.IsChecked)
            {
                return "GET";
            }
            else if ((bool)MethodPostRadioButton.IsChecked)
            {
                return "POST";
            }
            return "GET";
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            ResetButton.IsEnabled = false;

            this.ProcessTargets();

            _showDataViewModel.AccessibilityResults = [];

            var customAttrs = LoadHttpCustomAttributes();

            var checkHealthArgs = new object[4];
            checkHealthArgs[0] = int.Parse(TotalCount.Text);
            checkHealthArgs[1] = TargetsBox.Text.Split("\n");
            checkHealthArgs[2] = GetHttpMethod();
            checkHealthArgs[3] = customAttrs;

            _checkHealthWorker.RunWorkerAsync(checkHealthArgs);
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _checkHealthWorker.CancelAsync();

            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            ResetButton.IsEnabled = true;
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            // 重置自定义选项
            ToggleCustomHeaders.IsChecked = false;
            ToggleCustomCookies.IsChecked = false;
            ToggleCustomUA.IsChecked = false;

            // 重置请求方法
            MethodGetRadioButton.IsChecked = true;
            MethodPostRadioButton.IsChecked = false;
            Checked301or302.IsChecked = false;

            // 重置任务进度
            CurrentCount.Text = "0";
            TotalCount.Text = "0";

            // 重置结果列表
        }

        private void CopyToClipboardButton_Click(object sender, RoutedEventArgs e)
        {
            var clipboardText = new StringBuilder();

            // 获取列标题
            foreach (var column in ResultsDataGrid.Columns)
            {
                if (column.Header is TextBlock headerTextBlock)
                {
                    clipboardText.Append(headerTextBlock.Text + "\t");
                }
                else
                {
                    clipboardText.Append(column.Header.ToString() + "\t");
                }
            }
            clipboardText.AppendLine();

            // 获取行数据
            foreach (var item in ResultsDataGrid.Items)
            {
                var row = ResultsDataGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
                if (row != null)
                {
                    foreach (var column in ResultsDataGrid.Columns)
                    {
                        if (column.GetCellContent(row) is TextBlock cellContent)
                        {
                            clipboardText.Append(cellContent.Text + "\t");
                        }
                    }
                    clipboardText.AppendLine();
                }
            }

            Clipboard.SetText(clipboardText.ToString());
            MessageBox.Show("结果列表已复制到剪贴板");
        }

        private void InitCheckHealthBackgroundWorker()
        {
            _checkHealthWorker ??= new BackgroundWorker();

            _checkHealthWorker.WorkerReportsProgress = true;
            _checkHealthWorker.WorkerSupportsCancellation = true;
            _checkHealthWorker.DoWork += new DoWorkEventHandler(CheckHealthWorker_DoWork);
            _checkHealthWorker.ProgressChanged += new ProgressChangedEventHandler(CheckHealthWorker_ProgressChanged);
            _checkHealthWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(CheckHealthWorker_RunWorkerCompleted);
        }

        private void CheckHealthWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            var receivedArgs = e.Argument as object[];
            var totalCount = (int)receivedArgs[0];
            var targets = (string[])receivedArgs[1];
            var httpMethod = (string)receivedArgs[2];
            var customAttrs = (string[])receivedArgs[3];

            if (totalCount < 1)
            {
                _checkHealthWorker.ReportProgress(-1);
                return;
            }

            // 耗时操作
            for (var i = 0; i < totalCount; i++)
            {
                var work = new Cancel_CheckHealth(CheckTargetHealth);
                var target = targets[i].Trim();
                var task = Task.Run(() => work.Invoke(httpMethod, target, customAttrs[0], customAttrs[1], customAttrs[2]));
                while (!task.IsCompleted)
                {
                    //没完成
                    //判断是否取消了backgroundworker异步操作
                    if (_checkHealthWorker.CancellationPending)
                    {
                        //如何是  马上取消backgroundwork操作(这个地方才是真正取消) 
                        e.Cancel = true;
                        return;
                    }
                }

                var results = task.Result;
                var accessibilityResult = new Models.AccessibilityResult()
                {
                    Id = i + 1,
                    Target = target,
                    Url = (string)results[0],
                    AccessStateDesc = (string)results[1],
                    WebTitle = (string)results[2]
                };
                _checkHealthWorker.ReportProgress(i + 1, accessibilityResult);
            }

            e.Result = true;
        }

        private void CheckHealthWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            if (e.ProgressPercentage == -1)
            {
                MessageBox.Show("目标为空，请确认已添加目标");
                return;
            }
            CurrentCount.Text = e.ProgressPercentage.ToString();
            ProgressBar.Value = e.ProgressPercentage;
            var result = (Models.AccessibilityResult)e.UserState;
            if (result == null) return;
            _showDataViewModel.AccessibilityResults.Add(result);
        }

        private void CheckHealthWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled)
            {
                MessageBox.Show("任务已停止");
            }
            else if (e.Error != null)
            {
                MessageBox.Show("任务出现错误");
            }
            else
            {
                // 更新UI或执行其他UI相关操作
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                ResetButton.IsEnabled = true;
            }
        }

        private delegate object[] Cancel_CheckHealth(string httpMethord, string target, string customHeaders, string customCookies, string customUA);

        private object[] CheckTargetHealth(string httpMethord, string target, string customHeaders, string customCookies, string customUA)
        {
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = delegate { return true; };
            var httpClient = new HttpClient(handler);
            httpClient.Timeout = TimeSpan.FromSeconds(3);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0");

            UpdateHttpCustomAttributes(httpClient, customHeaders, customCookies, customUA);

            var htmlWeb = new HtmlWeb();
            var httpTargets = new List<string>();

            if (!target.StartsWith("http"))
            {
                httpTargets.Add($"http://{target}");
                httpTargets.Add($"https://{target}");
            }
            else
            {
                httpTargets.Add(target);
            }

            object[] results = { "无", "无法访问(0/1)", "N/A" };
            if (httpMethord.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var httpTarget in httpTargets)
                {
                    try
                    {
                        var title = "N/A";
                        try
                        {
                            results[0] = httpTarget;

                            var response = httpClient.GetAsync(httpTarget).Result;
                            results[1] = "稳定访问(1/1)";

                            try
                            {
                                var content = response.Content.ReadAsStringAsync().Result;
                                var htmlDoc = new HtmlDocument();
                                htmlDoc.LoadHtml(content);
                                var node = htmlDoc.DocumentNode.SelectSingleNode("//head/title");
                                if (node != null)
                                {
                                    title = node.InnerText;
                                    results[2] = title;
                                }
                            }
                            catch (Exception)
                            {
                            }
                            return results;
                        }
                        catch (Exception)
                        {
                        }
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                }
            }
            else if (httpMethord.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var httpTarget in httpTargets)
                {
                    try
                    {
                        var title = "N/A";
                        try
                        {
                            results[0] = httpTarget;

                            var response = httpClient.PostAsync(httpTarget, null).Result;
                            results[1] = "稳定访问(1/1)";

                            try
                            {
                                var content = response.Content.ReadAsStringAsync().Result;
                                var htmlDoc = new HtmlDocument();
                                htmlDoc.LoadHtml(content);
                                var node = htmlDoc.DocumentNode.SelectSingleNode("//head/title");
                                if (node != null)
                                {
                                    title = node.InnerText;
                                    results[2] = title;
                                }
                            }
                            catch (Exception)
                            {
                            }
                            return results;
                        }
                        catch (Exception)
                        {
                        }
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                }
            }

            return results;
        }

        private string[] LoadHttpCustomAttributes()
        {
            string[] customAttrs = {"", "", ""};

            if ((bool)ToggleCustomHeaders.IsChecked)
            {
                var customHeaders = CustomHeaders.Text;
                customAttrs[0] = customHeaders;
            }

            if ((bool)ToggleCustomCookies.IsChecked)
            {
                var customCookies = CustomCookies.Text;
                customAttrs[1] = customCookies;
            }

            if ((bool)ToggleCustomUA.IsChecked)
            {
                var customUA = CustomUA.Text;
                customAttrs[2] = customUA;
            }

            return customAttrs;
        }

        private void UpdateHttpCustomAttributes(HttpClient httpClient, string customHeaders, string customCookies, string customUA)
        {
            Debug.WriteLine(customHeaders);
            Debug.WriteLine(customCookies);
            Debug.WriteLine(customUA);
            if (!string.IsNullOrWhiteSpace(customHeaders))
            {
                var customHeaderArray = customHeaders.Split("\n");

                foreach (var customHeader in customHeaderArray)
                {
                    var headerPair = customHeader.Split(":");
                    httpClient.DefaultRequestHeaders.Add(headerPair[0].Trim(), headerPair[1].Trim());
                }
            }

            if (!string.IsNullOrWhiteSpace(customCookies))
            {
                httpClient.DefaultRequestHeaders.Add("Cookie", customCookies);
            }

            if (!string.IsNullOrWhiteSpace(customUA))
            {
                httpClient.DefaultRequestHeaders.UserAgent.Clear();
                httpClient.DefaultRequestHeaders.Add("User-Agent", customUA);
            }
        }
    }



    [ValueConversion(typeof(string), typeof(string))]
    class ColorConverter : IValueConverter
    {
        public object Convert(object value, Type typeTarget, object param, CultureInfo culture)
        {
            string strValue = value.ToString() ?? "";
            if (strValue.StartsWith("稳定访问"))
            {
                return "Green";
            }
            else if (strValue.StartsWith("不稳定访问"))
            {
                return "Orange";
            }
            else if (strValue.StartsWith("无法访问"))
            {
                return "Red";
            }
            return "Black";


        }
        public object ConvertBack(object value, Type typeTarget, object param, CultureInfo culture)
        {
            return "";
        }
    }
}