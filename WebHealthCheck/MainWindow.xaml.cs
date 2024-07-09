using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Net.Http;
using System.Globalization;
using System.Windows.Data;
using System.Text;
using System.Web;
using System.Windows.Threading;

using HtmlAgilityPack;
using OfficeOpenXml;

using WebHealthCheck.Models;
using WebHealthCheck.ViewModels;


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

        private DispatcherTimer _taskDispatcherTimer;
        private int taskElapsedTimeSeconds = 0;

        public MainWindow()
        {
            InitializeComponent();

            InitTaskElapsedTimeDispatcherTimer();

            DataContext = _showDataViewModel = new ShowDataViewModel();
        }

        private void InitTaskElapsedTimeDispatcherTimer()
        {
            _taskDispatcherTimer = new()
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _taskDispatcherTimer.Tick += TaskDispatcherTimer_Tick;
        }

        private void TaskDispatcherTimer_Tick(object? sender, EventArgs e)
        {
            taskElapsedTimeSeconds += 1;
            var elapsedTimeFriendly = new TimeSpan(0, 0, taskElapsedTimeSeconds).ToString(@"hh\:mm\:ss");
            TaskElapsedTimeTextBlock.Text = elapsedTimeFriendly;
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

            var targetText = TargetsBox.Text.Trim();
            var targetArray = string.IsNullOrWhiteSpace(targetText) ? [] : targetText.Split("\n");

            if (targetArray.Length < 1)
            {
                return targets;
            }

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
            var tryCount = (int)TryCountPerTarget.Value;
            var requestTimeout = RequestTimeout.Value;
            var threadCount = (int)ThreadCount.Value;


            if (targets.Count < 1)
            {
                MessageBox.Show("目标为空，请确认已添加目标");
                return;
            }

            TargetsBox.Text = string.Join("\n", targets);

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
            _checkHealthBackgroundWorker.RunWorkerAsync(new { targets, httpMethod, httpCustomAttrs, tryCount, requestTimeout, token = _checkHealthCancellationTokenSource.Token });

            TaskElapsedTimeTextBlock.Text = "00:00:00";
            taskElapsedTimeSeconds = 0;
            _taskDispatcherTimer.Start();
        }

        private void CheckHealthBackgroundWorker_DoWork(object? sender, DoWorkEventArgs e)
        {
            var arg = e.Argument as dynamic;
            if (arg == null) return;

            var targets = (List<string>)arg.targets;
            var httpMethod = (string)arg.httpMethod;
            var httpCustomAttrs = (HttpCustomAtrributes)arg.httpCustomAttrs;
            var tryCount = (int)arg.tryCount;
            var requestTimeout = (double)arg.requestTimeout;
            var token = (CancellationToken)arg.token;

            if (targets.Count < 1)
            {
                _checkHealthBackgroundWorker.ReportProgress(-1);
                return;
            }

            try
            {
                var task = ProcessTargets(targets, httpMethod, httpCustomAttrs.CustomHeaders, httpCustomAttrs.CustomCookies, httpCustomAttrs.CustomUA, tryCount, requestTimeout, token);
                task.Wait();
            }
            catch (OperationCanceledException)
            {
                e.Cancel = true;
            }
            catch (AggregateException ex)
            {
                if (ex.InnerExceptions.All(inner => inner is TaskCanceledException))
                {
                    e.Cancel = true;
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                Semaphore.Dispose();
            }
        }

        private async Task ProcessTargets(List<string> targets, string httpMethod, string customHeaders, string customCookies, string customUA, int tryCount, double requestTimeout, CancellationToken token)
        {
            var tasks = new List<Task>();

            foreach (var target in targets.Select((value, index) => new { value, index }))
            {
                if (_checkHealthBackgroundWorker.CancellationPending)
                {
                    token.ThrowIfCancellationRequested();
                }

                await Semaphore.WaitAsync(token);

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var result = await CheckTargetHealthAsync(target.value, httpMethod, customHeaders, customCookies, customUA, tryCount, requestTimeout, token);
                        var accessibilityResult = new Models.AccessibilityResult()
                        {
                            Id = target.index + 1,
                            Target = target.value,
                            Url = result.Url,
                            AccessStateDesc = result.State,
                            WebTitle = result.WebTitle,
                            WebContent = result.WebContent,
                        };
                        _checkHealthBackgroundWorker.ReportProgress(accessibilityResult.Id, accessibilityResult);
                    }
                    finally
                    {
                        Semaphore.Release();
                    }
                }, token));
            }

            await Task.WhenAll(tasks);
        }

        private void CheckHealthBackgroundWorker_ProgressChanged(object? sender, ProgressChangedEventArgs e)
        {
            if (e.UserState is not Models.AccessibilityResult result) return;
            var index = GetTargetIndexFromAccessibilityResults(result);
            _showDataViewModel.AccessibilityResults.Insert(index, result);

            CurrentCount.Text = _showDataViewModel.AccessibilityResults.Count.ToString();
            ProgressBar.Value = _showDataViewModel.AccessibilityResults.Count;
        }

        private void CheckHealthBackgroundWorker_RunWorkerCompleted(object? sender, RunWorkerCompletedEventArgs e)
        {
            _taskDispatcherTimer.Stop();

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
                MessageBox.Show($"任务已完成，用时：{TaskElapsedTimeTextBlock.Text}");

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
            TaskElapsedTimeTextBlock.Text = "00:00:00";
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
                clipboardText.Append(item.Id + "\t" + item.Target + "\t" + item.Url + "\t" + item.AccessStateDesc + "\t" + item.WebTitle + "\t" + item.WebContent);
                clipboardText.AppendLine();
            }

            Clipboard.SetText(clipboardText.ToString());

            clipboardText.Clear();
            CallGC();
            MessageBox.Show("结果列表已复制到剪贴板");
        }

        private async Task<CheckHealthResult> CheckTargetHealthAsync(string target, string httpMethod, string customHeaders, string customCookies, string customUA, int tryCount, double requestTimeout, CancellationToken token)
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
                var successCount = 0;

                for (var i = 0; i < tryCount; i++)
                {
                    var handler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = delegate { return true; },
                        AutomaticDecompression = System.Net.DecompressionMethods.GZip
                    };

                    using var httpClient = new HttpClient(handler);
                    httpClient.Timeout = TimeSpan.FromSeconds(requestTimeout);

                    UpdateHttpCustomAttributes(httpClient, customHeaders, customCookies, customUA);

                    try
                    {

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

                        request.RequestUri = new Uri(url);
                        var response = await httpClient.SendAsync(request, token);
                        result.Url = url;
                        successCount += 1;

                        var content = await response.Content.ReadAsStringAsync(token);
                        result.WebContent = content.Trim();
                        try
                        {
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
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"请求 {url} 时发生错误: {ex.Message}");
                    }
                }

                if (successCount == 0)
                {
                    result.State = $"无法访问({successCount}/{tryCount})";
                    continue;
                }
                else if (successCount == tryCount)
                {
                    result.State = $"稳定访问({successCount}/{tryCount})";
                    return result;
                }
                else
                {
                    result.State = $"不稳定访问({successCount}/{tryCount})";
                    return result;
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

        private void CopySelectedRow_Click(object sender, RoutedEventArgs e)
        {
            if (ResultsDataGrid.SelectedItem != null)
            {
                Models.AccessibilityResult selectedItem = (Models.AccessibilityResult)ResultsDataGrid.SelectedItem;

                var clipboardText = new StringBuilder();
                clipboardText.Append(selectedItem.Id + "\t" + selectedItem.Target + "\t" + selectedItem.Url + "\t" + selectedItem.AccessStateDesc + "\t" + selectedItem.WebTitle + "\t" + selectedItem.WebContent);
                Clipboard.SetText(clipboardText.ToString());
                clipboardText.Clear();
                MessageBox.Show("复制成功");
            }
        }

        private void ExportToExcelButton_Click(object sender, RoutedEventArgs e)
        {
            ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;

            using var excelPackage = new ExcelPackage();
            var worksheet = excelPackage.Workbook.Worksheets.Add("结果列表");

            worksheet.Cells["A1"].Value = "序号";
            worksheet.Cells["B1"].Value = "目标";
            worksheet.Cells["C1"].Value = "URL";
            worksheet.Cells["D1"].Value = "访问状态";
            worksheet.Cells["E1"].Value = "网页标题";
            worksheet.Cells["F1"].Value = "网页内容";

            // 获取行数据
            var startRow = 2;
            foreach (var item in _showDataViewModel.AccessibilityResults)
            {
                worksheet.Cells[$"A{startRow}"].Value = item.Id;
                worksheet.Cells[$"B{startRow}"].Value = item.Target;
                worksheet.Cells[$"C{startRow}"].Value = item.Url;
                worksheet.Cells[$"D{startRow}"].Value = item.AccessStateDesc;
                worksheet.Cells[$"E{startRow}"].Value = item.WebTitle;
                worksheet.Cells[$"F{startRow}"].Value = item.WebContent;

                startRow += 1;
            }

            // 检查并转义特殊字符
            for (int row = 1; row < startRow; row++)
            {
                string cellValue = worksheet.Cells[row, 1].Text;
                worksheet.Cells[row, 1].Value = System.Security.SecurityElement.Escape(cellValue);
            }

            var currentTimestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
            var saveFileDialog = new SaveFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx|All files (*.*)|*.*",
                FilterIndex = 1,
                FileName = $"结果列表_{currentTimestamp}.xlsx"
            };

            var submitSave = saveFileDialog.ShowDialog();

            if (submitSave == true)
            {
                var filePath = saveFileDialog.FileName;
                var excelFileInfo = new FileInfo(filePath);

                excelPackage.SaveAs(excelFileInfo);

                MessageBox.Show($"导出成功，路径为：{filePath}");
            }

            excelPackage.Dispose();

            CallGC();
        }

        private void CallGC()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            catch (Exception)
            {
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
        public string State { get; set; } = "无法访问";
        public string WebTitle { get; set; } = "";
        public string WebContent { get; set; } = "";
    }
}