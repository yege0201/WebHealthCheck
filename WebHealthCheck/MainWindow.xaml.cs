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
using WebHealthCheck.Models;
using System.Web;
using System.Security.Policy;
using System.Threading;
using System.Collections.Concurrent;
using System;

namespace WebHealthCheck
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly ShowDataViewModel _showDataViewModel;

        private SemaphoreSlim Semaphore;
        private BackgroundWorker _checkHealthBackgroundWorker;
        private CancellationTokenSource _checkHealthCancellationTokenSource;

        public MainWindow()
        {
            InitializeComponent();

            DataContext = _showDataViewModel = new ShowDataViewModel();
        }

        private async void ImportFromFileButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
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

        private void ClearTargetsButton_Click(object sender, RoutedEventArgs e)
        {
            TargetsBox.Text = "";
            MessageBox.Show("目标列表已清空");
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

        private List<string> GetTargets()
        {
            var targets = new List<string>();
            var validCount = 0;
            var emptyCount = 0;

            var targetArray = TargetsBox.Text.Split("\n");

            foreach (var target in targetArray)
            {
                if (string.IsNullOrEmpty(target.Trim()))
                {
                    emptyCount++;
                    continue;
                }
                validCount++;
                targets.Add(target.Trim());
            }

            TotalCount.Text = validCount.ToString();

            MessageBox.Show($"获取到有效目标 {validCount} 个，空行 {emptyCount} 个。");

            return targets;
        }

        private string GetHttpMethod()
        {
            if (MethodGetRadioButton.IsChecked == true)
            {
                return "GET";
            }
            else if (MethodPostRadioButton.IsChecked == true)
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

            _showDataViewModel.AccessibilityResults = [];

            var targets = GetTargets();
            var httpMethod = GetHttpMethod();
            var httpCustomAttrs = GetHttpCustomAttributes();
            var requestTimeout = RequestTimeout.Value;
            var threadCount = (int)ThreadCount.Value;

            Semaphore = new SemaphoreSlim(threadCount);

            _checkHealthBackgroundWorker = new BackgroundWorker
            {
                WorkerSupportsCancellation = true,
                WorkerReportsProgress = true
            };
            _checkHealthBackgroundWorker.DoWork += CheckHealthBackgroundWorker_DoWork;
            _checkHealthBackgroundWorker.ProgressChanged += CheckHealthBackgroundWorker_ProgressChanged;
            _checkHealthBackgroundWorker.RunWorkerCompleted += CheckHealthBackgroundWorker_RunWorkerCompleted;
            _checkHealthCancellationTokenSource = new CancellationTokenSource();
            _checkHealthBackgroundWorker.RunWorkerAsync(new { targets, httpMethod, httpCustomAttrs, requestTimeout, token = _checkHealthCancellationTokenSource.Token });
        }

        private void CheckHealthBackgroundWorker_DoWork(object? sender, DoWorkEventArgs e)
        {
            var arg = e.Argument as dynamic;
            if (arg == null) return;

            var targets = (List<string>)arg.targets;
            var httpMethod = (string)arg.httpMethod;
            var httpCustomAttrs = (HttpCustomAtrributes)arg.httpCustomAttrs;
            var requestTimeout = (double)arg.requestTimeout;
            var token = (CancellationToken)arg.token;

            if (targets.Count < 1)
            {
                _checkHealthBackgroundWorker.ReportProgress(-1);
                return;
            }

            var tasks = new List<Task>();

            foreach (var target in targets.Select((value, index) => new { value, index }))
            {
                if (_checkHealthBackgroundWorker.CancellationPending)
                {
                    e.Cancel = true;
                    break;
                }

                tasks.Add(Task.Run(async () =>
                {
                    await Semaphore.WaitAsync(token);
                    try
                    {
                        var result = await CheckTargetHealthAsync(target.value, httpMethod, httpCustomAttrs.CustomHeaders, httpCustomAttrs.CustomCookies, httpCustomAttrs.CustomUA, requestTimeout, token);
                        var accessibilityResult = new Models.AccessibilityResult()
                        {
                            Id = target.index + 1,
                            Target = target.value,
                            Url = result.Url,
                            AccessStateDesc = result.State,
                            WebTitle = result.WebTitle,
                            WebContent = ""
                        };
                        _checkHealthBackgroundWorker.ReportProgress(accessibilityResult.Id, accessibilityResult);
                    }
                    finally
                    {
                        Semaphore.Release();
                    }
                }, token));
            }

            try
            {
                Task.WhenAll(tasks).Wait(token);
            }
            catch (OperationCanceledException)
            {
                e.Cancel = true;
            }
        }

        private void CheckHealthBackgroundWorker_ProgressChanged(object? sender, ProgressChangedEventArgs e)
        {
            if (e.ProgressPercentage == -1)
            {
                MessageBox.Show("目标为空，请确认已添加目标");
                return;
            }

            if (e.UserState is not Models.AccessibilityResult result) return;
            var index = GetTargetIndexFromAccessibilityResults(result);
            _showDataViewModel.AccessibilityResults.Insert(index, result);

            CurrentCount.Text = _showDataViewModel.AccessibilityResults.Count.ToString();
            ProgressBar.Value = _showDataViewModel.AccessibilityResults.Count;
        }

        private void CheckHealthBackgroundWorker_RunWorkerCompleted(object? sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled)
            {
                MessageBox.Show("任务已取消");
            }
            else if (e.Error != null)
            {
                MessageBox.Show($"任务发生错误: {e.Error.Message}");
            }
            else
            {
                MessageBox.Show("任务已完成");

                // 重置任务操作按钮
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                ResetButton.IsEnabled = true;
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (_checkHealthBackgroundWorker != null && _checkHealthBackgroundWorker.IsBusy)
            {
                _checkHealthBackgroundWorker.CancelAsync();
                _checkHealthCancellationTokenSource.Cancel();
            }

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
            ProgressBar.Value = 0;
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
            foreach (var item in _showDataViewModel.AccessibilityResults)
            {
                clipboardText.Append(item.Id + "\t" + item.Target + "\t" + item.Url + "\t" + item.AccessStateDesc + "\t" + item.WebTitle);
                clipboardText.AppendLine();
            }

            Clipboard.SetText(clipboardText.ToString());
            MessageBox.Show("结果列表已复制到剪贴板");
        }

        private async Task<CheckHealthResult> CheckTargetHealthAsync(string target, string httpMethod, string customHeaders, string customCookies, string customUA, double requestTimeout, CancellationToken token)
        {
            var urls = new List<string>();
            if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                urls.Add(target);
            }
            else
            {
                urls.Add($"https://{target}");
                urls.Add($"http://{target}");
            }

            var result = new CheckHealthResult();

            foreach (var url in urls)
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = delegate { return true; }
                };

                using var httpClient = new HttpClient(handler);
                httpClient.Timeout = TimeSpan.FromSeconds(requestTimeout);

                UpdateHttpCustomAttributes(httpClient, customHeaders, customCookies, customUA);

                var request = new HttpRequestMessage();

                if (httpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    request.Method = HttpMethod.Get;
                }
                else if (httpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    request.Method = HttpMethod.Post;
                }
                else
                {
                    request.Method = HttpMethod.Get;
                }

                try
                {
                    request.RequestUri = new Uri(url);
                    var response = await httpClient.SendAsync(request, token);
                    result.Url = url;
                    result.State = "稳定访问(1/1)";
                    try
                    {
                        var content = await response.Content.ReadAsStringAsync(token);
                        var htmlDoc = new HtmlDocument();
                        htmlDoc.LoadHtml(content);
                        var node = htmlDoc.DocumentNode.SelectSingleNode("//head/title");
                        if (node != null)
                        {
                            result.WebTitle = HttpUtility.HtmlDecode(node.InnerText).Trim();
                        }
                    }
                    catch (Exception)
                    {
                    }
                    Debug.WriteLine($"请求 {url} 成功");
                    return result;
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine($"请求 {url} 被取消");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"请求 {url} 时发生错误: {ex.Message}");
                }
            }

            return result;
        }

        private int GetTargetIndexFromAccessibilityResults(AccessibilityResult result)
        {
            if (_showDataViewModel.AccessibilityResults.Count == 0) return 0;
            for (int i = 0; i < _showDataViewModel.AccessibilityResults.Count; i++)
            {
                if (result.Id < _showDataViewModel.AccessibilityResults[i].Id) return i;
            }
            return _showDataViewModel.AccessibilityResults.Count;
        }

        private string GetHtmlEncoding(HtmlDocument htmlDoc)
        {
            var node = htmlDoc.DocumentNode.SelectSingleNode("//head/meta[@http-equiv=\"Content-Type\"]");
            if (node == null)
            {
                return "UTF-8";
            }
            var attributeValue = node.GetAttributeValue("content", "text/html; charset=UTF-8");
            var attributeValueSplit = attributeValue.Split(";");
            foreach (var _value in attributeValueSplit)
            {
                var value = _value.Trim();
                if (!value.Contains('='))
                {
                    continue;
                }
                var valueSlices = value.Split("=");
                if (valueSlices[0].Equals("charset", StringComparison.OrdinalIgnoreCase))
                {
                    return valueSlices[1];
                }
            }
            return "UTF-8";
        }

        public string ConvertToUTF8(string content, string fromCharset)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding utf8 = Encoding.GetEncoding("utf-8");
            Encoding encodingFromCharset = Encoding.GetEncoding(fromCharset);
            byte[] result = encodingFromCharset.GetBytes(content);
            result = Encoding.Convert(encodingFromCharset, utf8, result);
            return utf8.GetString(result);
        }

        private HttpCustomAtrributes GetHttpCustomAttributes()
        {
            var customAttrs = new HttpCustomAtrributes();

            if (ToggleCustomHeaders.IsChecked == true)
            {
                customAttrs.CustomHeaders = CustomHeaders.Text.Trim();
            }
            if (ToggleCustomCookies.IsChecked == true)
            {
                customAttrs.CustomCookies = CustomCookies.Text.Trim();
            }
            if (ToggleCustomUA.IsChecked == true)
            {
                customAttrs.CustomUA = CustomUA.Text.Trim();
            }

            return customAttrs;
        }

        private void UpdateHttpCustomAttributes(HttpClient httpClient, string customHeaders, string customCookies, string customUA)
        {
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

    public class HttpCustomAtrributes
    {
        public string CustomHeaders { get; set; } = "";
        public string CustomCookies { get; set; } = "";
        public string CustomUA { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0";
    }

    public class CheckHealthResult
    {
        public string Url { get; set; } = "无";
        public string State { get; set; } = "无法访问(1/1)";
        public string WebTitle { get; set; } = "";
        public string WebContent { get; set; } = "";
    }
}