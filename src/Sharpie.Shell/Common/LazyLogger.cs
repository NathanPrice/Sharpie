using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Sharpie.Shell.Common;

/// <summary>
/// Forwards events to a logger that is built the first time one is written.
/// </summary>
/// <remarks>
/// Building the file logger up front added about 28 ms to every shell start (50 ms to 78 ms for <c>-c true</c>), and
/// most shells never log anything. Serilog's level methods such as <see cref="ILogger.Warning(string)" /> all end in
/// the two <c>Write</c> overloads below.
/// </remarks>
/// <param name="create">Builds the real logger; called at most once.</param>
internal sealed class LazyLogger(Func<Logger> create) : ILogger, IDisposable
{
	private readonly Lazy<Logger> logger = new(create);

	/// <summary>
	/// Gets a value that indicates whether the real logger has been built.
	/// </summary>
	/// <value>
	/// <see langword="true" /> once an event has been written; otherwise, <see langword="false" />.
	/// </value>
	internal bool IsCreated => this.logger.IsValueCreated;

	/// <inheritdoc />
	public void Write(LogEvent logEvent) => this.logger.Value.Write(logEvent);

	/// <inheritdoc />
	public void Write(LogEventLevel level, Exception? exception, string messageTemplate, params object?[]? propertyValues) =>
		this.logger.Value.Write(level, exception, messageTemplate, propertyValues);

	/// <summary>
	/// Flushes and closes the real logger if it was built, without building it just to close it.
	/// </summary>
	public void Dispose()
	{
		if (this.logger.IsValueCreated)
		{
			this.logger.Value.Dispose();
		}
	}
}
