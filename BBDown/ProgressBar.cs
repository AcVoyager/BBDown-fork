using System;
using System.Text;
using System.Threading;

/**
 * From https://gist.github.com/DanielSWolf/0ab6a96899cc5377bf54
 */
namespace BBDown;

class ProgressBar : IDisposable, IProgress<double>
{
	private const int blockCount = 40;
	private readonly TimeSpan animationInterval = TimeSpan.FromSeconds(1.0 / 8);
	private const string animation = @"|/-\";

	private readonly Timer timer;

	private double currentProgress = 0;
	private string currentText = string.Empty;
	private bool disposed = false;
	private int animationIndex = 0;

	//速度计算
	private readonly TimeSpan speedCalcInterval = TimeSpan.FromSeconds(1);
	private long lastDownloadedBytes = 0;
	private long downloadedBytes = 0;
	private string speedString = "";
	private readonly Timer speedTimer;

	//服务器模式使用，更新下载任务的进度
	private DownloadTask? RelatedTask = null;

	// 输出被重定向且设置了环境变量 BBDOWN_PROGRESS_REPORT=1 时,
	// 每秒输出一行机器可读的进度: "[BBDOWN_PROGRESS] <百分比> <每秒字节数>", 供外部程序(如 BBDownServer)解析
	private static readonly bool LineReport = Console.IsOutputRedirected
		&& Environment.GetEnvironmentVariable("BBDOWN_PROGRESS_REPORT") == "1";
	private const int lineReportEveryTicks = 8; // 动画间隔为1/8秒, 即每秒输出一次
	private int lineReportTick = 0;
	private long lastSpeedBytes = 0;

	public ProgressBar(DownloadTask? task = null)
	{
		timer = new Timer(TimerHandler);
		speedTimer = new Timer(SpeedTimerHandler);
		if (task is not null) RelatedTask = task;
		// A progress bar is only for temporary display in a console window.
		// If the console output is redirected to a file, draw nothing.
		// Otherwise, we'll end up with a lot of garbage in the target file.
		// However, if this progressbar is for a server download task,
		// we still need it to report progress no matter where stdout is redirected.
		// The prevention of writing garbage should be controlled on the methods do the actual writing.
		if (!Console.IsOutputRedirected || RelatedTask is not null || LineReport)
		{
			ResetTimer();
			ResetSpeedTimer();

		}
	}

	public void Report(double value)
	{
		// Make sure value is in [0..1] range
		value = Math.Max(0, Math.Min(1, value));
		Interlocked.Exchange(ref currentProgress, value);
	}

	public void Report(double value, long bytesCount)
	{
		// Make sure value is in [0..1] range
		value = Math.Max(0, Math.Min(1, value));
		Interlocked.Exchange(ref currentProgress, value);
		Interlocked.Exchange(ref downloadedBytes, bytesCount);
	}

	private void SpeedTimerHandler(object? state)
	{
		lock (speedTimer)
		{
			if (disposed) return;

			if (downloadedBytes > 0 && downloadedBytes - lastDownloadedBytes > 0)
			{
				var delta = downloadedBytes - lastDownloadedBytes;
				speedString = " - " + BBDownUtil.FormatFileSize(delta) + "/s";
				lastDownloadedBytes = downloadedBytes;
				Interlocked.Exchange(ref lastSpeedBytes, delta);
				if (RelatedTask is not null) 
				{
					RelatedTask.DownloadSpeed = delta;
					RelatedTask.TotalDownloadedBytes += delta;
				}
			}
			else
			{
				Interlocked.Exchange(ref lastSpeedBytes, 0);
			}

			ResetSpeedTimer();
		}
	}

	private void TimerHandler(object? state)
	{
		lock (timer)
		{
			if (disposed) return;

			int progressBlockCount = (int)(currentProgress * blockCount);
			double percent = currentProgress * 100;
			string text = string.Format("                            [{0}{1}] {2,3:0.00}% {3}{4}",
				new string('#', progressBlockCount), new string('-', blockCount - progressBlockCount), percent,
				animation[animationIndex++ % animation.Length],
				speedString);
			UpdateText(text);
			if (RelatedTask is not null) 
			{
				RelatedTask.Progress = currentProgress;
			}
			if (LineReport && lineReportTick++ % lineReportEveryTicks == 0)
			{
				// 单次调用写入整行, 尽量避免与其他日志输出交错
				Console.Out.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"[BBDOWN_PROGRESS] {percent:0.00} {Interlocked.Read(ref lastSpeedBytes)}{Environment.NewLine}"));
			}

			ResetTimer();
		}
	}

	private void UpdateText(string text)
	{
		// Write nothing when output is redirected
		if (Console.IsOutputRedirected) return;
		// Get length of common portion
		int commonPrefixLength = 0;
		int commonLength = Math.Min(currentText.Length, text.Length);
		while (commonPrefixLength < commonLength && text[commonPrefixLength] == currentText[commonPrefixLength])
		{
			commonPrefixLength++;
		}

		// Backtrack to the first differing character
		StringBuilder outputBuilder = new();
		outputBuilder.Append('\b', currentText.Length - commonPrefixLength);

		// Output new suffix
		outputBuilder.Append(text[commonPrefixLength..]);

		// If the new text is shorter than the old one: delete overlapping characters
		int overlapCount = currentText.Length - text.Length;
		if (overlapCount > 0)
		{
			outputBuilder.Append(' ', overlapCount);
			outputBuilder.Append('\b', overlapCount);
		}

		Console.Write(outputBuilder);
		currentText = text;
	}

	private void ResetTimer()
	{
		timer.Change(animationInterval, TimeSpan.FromMilliseconds(-1));
	}

	private void ResetSpeedTimer()
	{
		speedTimer.Change(speedCalcInterval, TimeSpan.FromMilliseconds(-1));
	}

	public void Dispose()
	{
		lock (timer)
		{
			if (LineReport && !disposed)
			{
				Console.Out.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"[BBDOWN_PROGRESS] {currentProgress * 100:0.00} 0{Environment.NewLine}"));
			}
			disposed = true;
			UpdateText(string.Empty);
		}
	}
}