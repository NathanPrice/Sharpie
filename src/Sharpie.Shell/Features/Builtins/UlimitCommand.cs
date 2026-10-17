using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using DotNetTips.Spargine.Core;
using Sharpie.Shell.Common;

namespace Sharpie.Shell.Features.Builtins;

/// <summary>
/// Prints or sets the shell's resource limits, which the programs it starts inherit.
/// </summary>
/// <remarks>
/// <c>ulimit</c> alone prints the file size limit. A letter picks another limit, a value after it sets that limit, and
/// <c>-a</c> prints every limit with its name and unit. <c>-H</c> picks the hard limit and <c>-S</c> the soft one.
/// Printing shows the soft limit unless only <c>-H</c> is given, and setting changes both unless one is picked, as in
/// bash. A value is a number in the unit the limit shows, <c>unlimited</c>, or <c>hard</c> or <c>soft</c> for the
/// limit's current hard or soft value. Windows has no resource limits, so there it's an error.
/// </remarks>
internal sealed class UlimitCommand : ICommand
{
	/// <summary>
	/// The resource number of the pipe size row, which isn't a limit but the fixed size of a pipe write.
	/// </summary>
	private const int PipeSize = -1;

	/// <summary>
	/// Every limit, in the order and with the names and units bash's <c>ulimit -a</c> shows.
	/// </summary>
	/// <remarks>
	/// The resource numbers come from Linux's and macOS's <c>sys/resource.h</c>. Open files, processes, locked memory
	/// and address space have different numbers on the two, and the rows with no macOS number don't exist there.
	/// </remarks>
	private static readonly Limit[] Table =
	[
		new('R', "real-time non-blocking time", "microseconds", 1, 15, null),
		new('c', "core file size", "blocks", 512, 4, 4),
		new('d', "data seg size", "kbytes", 1024, 2, 2),
		new('e', "scheduling priority", null, 1, 13, null),
		new('f', "file size", "blocks", 512, 1, 1),
		new('i', "pending signals", null, 1, 11, null),
		new('l', "max locked memory", "kbytes", 1024, 8, 6),
		new('m', "max memory size", "kbytes", 1024, 5, 5),
		new('n', "open files", null, 1, 7, 8),
		new('p', "pipe size", "512 bytes", 512, PipeSize, PipeSize),
		new('q', "POSIX message queues", "bytes", 1, 12, null),
		new('r', "real-time priority", null, 1, 14, null),
		new('s', "stack size", "kbytes", 1024, 3, 3),
		new('t', "cpu time", "seconds", 1, 0, 0),
		new('u', "max user processes", null, 1, 6, 7),
		new('v', "virtual memory", "kbytes", 1024, 9, 5),
		new('x', "file locks", null, 1, 10, null),
	];

	/// <inheritdoc />
	public string Name => "ulimit";

	/// <inheritdoc />
	public string Description => "Prints or sets resource limits";

	/// <inheritdoc />
	/// <remarks>
	/// The status is 0, 1 for a bad value, a limit that can't be read or changed, or on Windows, and 2 for an option
	/// that doesn't exist, as in bash. Limits before a failed one stay changed.
	/// </remarks>
	public int Run(string[] args)
	{
		_ = args.ArgumentNotNull();

		if (OperatingSystem.IsWindows())
		{
			new ShellError("sharpie::ulimit::unsupported", "`ulimit` doesn't work on Windows") { Help = "Windows has no per-process resource limits to change" }.Write();

			return 1;
		}

		var limits = Limits(OperatingSystem.IsMacOS());

		if (!TryParse(args, limits, out var request, out var error))
		{
			error.Write();

			return 2;
		}

		if (request.All)
		{
			foreach (var limit in limits)
			{
				if (TryGet(limit, out var soft, out var hard))
				{
					Console.WriteLine(Format(limit, request.ShowHard ? hard : soft, NativeMethods.ResourceUnlimited, describe: true));
				}
			}

			return 0;
		}

		foreach (var (limit, value) in request.Items)
		{
			if (!TryGet(limit, out var soft, out var hard))
			{
				return 1;
			}

			if (value is null)
			{
				Console.WriteLine(Format(limit, request.ShowHard ? hard : soft, NativeMethods.ResourceUnlimited, request.Items.Count > 1));
			}
			else if (!TryParseValue(value, limit.Factor, soft, hard, NativeMethods.ResourceUnlimited, out var amount, out error) || !TrySet(limit, request.SetSoft ? amount : soft, request.SetHard ? amount : hard, out error))
			{
				error.Write();

				return 1;
			}
		}

		return 0;
	}

	/// <summary>
	/// Gets the limits that exist on an OS.
	/// </summary>
	/// <param name="macOS"><see langword="true" /> for macOS; otherwise, <see langword="false" /> for Linux.</param>
	/// <returns>
	/// The limits, in bash's order.
	/// </returns>
	internal static IReadOnlyList<Limit> Limits(bool macOS) =>
		[.. Table.Where(limit => (macOS ? limit.MacOS : limit.Linux) is not null)
			.Select(limit => limit with { Resource = (macOS ? limit.MacOS : limit.Linux)!.Value })];

	/// <summary>
	/// Reads the options and values given to <c>ulimit</c>, as bash's option parser does.
	/// </summary>
	/// <param name="args">The arguments.</param>
	/// <param name="limits">The limits that exist on this OS.</param>
	/// <param name="request">Receives what to print or set.</param>
	/// <param name="error">Receives the option that doesn't exist, or <see langword="null" />.</param>
	/// <returns>
	/// <see langword="true" /> when every option exists; otherwise, <see langword="false" />.
	/// </returns>
	/// <remarks>
	/// A limit letter takes the rest of its word as its value, as in <c>-n256</c>, or else the next word when that
	/// doesn't start with <c>-</c>. A word after the options is the value of the last letter when it has none. No
	/// letter means <c>-f</c>. Later words are ignored, as bash ignores them.
	/// </remarks>
	internal static bool TryParse(string[] args, IReadOnlyList<Limit> limits, out Request request, [NotNullWhen(false)] out ShellError? error)
	{
		_ = args.ArgumentNotNull();

		request = new Request();
		error = null;

		var index = 0;

		for (; index < args.Length && args[index] is ['-', _, ..] option; index++)
		{
			if (option == "--")
			{
				index++;

				break;
			}

			for (var at = 1; at < option.Length; at++)
			{
				if (option[at] is 'H' or 'S' or 'a')
				{
					request.Hard |= option[at] == 'H';
					request.Soft |= option[at] == 'S';
					request.All |= option[at] == 'a';

					continue;
				}

				var letter = option[at];

				if (limits.FirstOrDefault(limit => limit.Letter == letter) is not { } limit)
				{
					error = new ShellError("sharpie::ulimit::invalid_option", $"`-{letter}` isn't an option of ulimit")
					{
						Help = $"ulimit takes -a, -H, -S and -{string.Concat(limits.Select(entry => entry.Letter))}",
					};

					return false;
				}

				var value = at + 1 < option.Length ? option[(at + 1)..]
					: index + 1 < args.Length && !args[index + 1].StartsWith('-') ? args[++index]
					: null;

				request.Items.Add((limit, value));

				break;
			}
		}

		if (request.Items is [])
		{
			request.Items.Add((limits.First(limit => limit.Letter == 'f'), null));
		}

		if (index < args.Length && request.Items[^1].Value is null)
		{
			request.Items[^1] = (request.Items[^1].Limit, args[index]);
		}

		return true;
	}

	/// <summary>
	/// Reads a value given to <c>ulimit</c> as a raw limit.
	/// </summary>
	/// <param name="text">A number in the limit's unit, <c>unlimited</c>, <c>hard</c> or <c>soft</c>.</param>
	/// <param name="factor">How many of the system's units one of the shown units is, like 1024 for kbytes.</param>
	/// <param name="soft">The current soft limit, which <c>soft</c> stands for.</param>
	/// <param name="hard">The current hard limit, which <c>hard</c> stands for.</param>
	/// <param name="unlimited">The OS's value for no limit.</param>
	/// <param name="value">Receives the raw limit.</param>
	/// <param name="error">Receives what's wrong with <paramref name="text" />, or <see langword="null" />.</param>
	/// <returns>
	/// <see langword="true" /> when <paramref name="text" /> is valid; otherwise, <see langword="false" />.
	/// </returns>
	internal static bool TryParseValue(string text, ulong factor, ulong soft, ulong hard, ulong unlimited, out ulong value, [NotNullWhen(false)] out ShellError? error)
	{
		_ = text.ArgumentNotNull();

		error = null;
		value = text switch
		{
			"unlimited" => unlimited,
			"hard" => hard,
			"soft" => soft,
			_ => 0,
		};

		if (text is "unlimited" or "hard" or "soft")
		{
			return true;
		}

		if (text is [] || !text.All(char.IsAsciiDigit))
		{
			error = new ShellError("sharpie::ulimit::not_a_number", $"`{text}` isn't a number")
			{
				Help = "give a whole number, unlimited, hard or soft",
			};

			return false;
		}

		if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count > ulong.MaxValue / factor)
		{
			error = new ShellError("sharpie::ulimit::out_of_range", $"`{text}` is too big for a limit")
			{
				Help = "use unlimited for no limit",
			};

			return false;
		}

		value = count * factor;

		return true;
	}

	/// <summary>
	/// Writes a limit the way <c>ulimit</c> prints it.
	/// </summary>
	/// <param name="limit">The limit.</param>
	/// <param name="value">Its raw value.</param>
	/// <param name="unlimited">The OS's value for no limit.</param>
	/// <param name="describe">
	/// <see langword="true" /> to start with the limit's name, unit and letter, as <c>ulimit -a</c> does; otherwise,
	/// <see langword="false" /> for the value alone.
	/// </param>
	/// <returns>
	/// The text, with the value in the limit's unit or <c>unlimited</c>.
	/// </returns>
	internal static string Format(Limit limit, ulong value, ulong unlimited, bool describe)
	{
		_ = limit.ArgumentNotNull();

		var text = value == unlimited ? "unlimited" : (value / limit.Factor).ToString(CultureInfo.InvariantCulture);

		if (!describe)
		{
			return text;
		}

		var units = limit.Units is null ? $"(-{limit.Letter}) " : $"({limit.Units}, -{limit.Letter}) ";

		// bash's printf("%-20s %20s"), so a long name pushes the rest along instead of being cut.
		return $"{limit.Description,-20} {units,20}{text}";
	}

	/// <summary>
	/// Reads a limit's soft and hard values, and reports when it can't.
	/// </summary>
	/// <param name="limit">The limit.</param>
	/// <param name="soft">Receives the soft limit.</param>
	/// <param name="hard">Receives the hard limit.</param>
	/// <returns>
	/// <see langword="true" /> when the limit was read; otherwise, <see langword="false" />.
	/// </returns>
	private static bool TryGet(Limit limit, out ulong soft, out ulong hard)
	{
		if (limit.Resource == PipeSize)
		{
			soft = hard = (ulong)NativeMethods.PipeBufferSize;

			return true;
		}

		var failed = NativeMethods.GetResourceLimit(limit.Resource, out var value) != 0;

		soft = value.Current;
		hard = value.Maximum;

		if (failed)
		{
			new ShellError("sharpie::ulimit::cannot_read", $"Can't read the {limit.Description} limit") { Help = Marshal.GetLastPInvokeErrorMessage() }.Write();
		}

		return !failed;
	}

	/// <summary>
	/// Changes a limit.
	/// </summary>
	/// <param name="limit">The limit.</param>
	/// <param name="soft">The new soft limit.</param>
	/// <param name="hard">The new hard limit.</param>
	/// <param name="error">Receives why the system refused, or <see langword="null" />.</param>
	/// <returns>
	/// <see langword="true" /> when the limit changed; otherwise, <see langword="false" />.
	/// </returns>
	private static bool TrySet(Limit limit, ulong soft, ulong hard, [NotNullWhen(false)] out ShellError? error)
	{
		// The pipe size is fixed, and bash reports EINVAL for it, which is 22 on Linux and macOS alike.
		var reason = limit.Resource == PipeSize ? Marshal.GetPInvokeErrorMessage(22)
			: NativeMethods.SetResourceLimit(limit.Resource, new NativeMethods.ResourceLimit((nuint)soft, (nuint)hard)) != 0 ? Marshal.GetLastPInvokeErrorMessage()
			: null;

		error = reason is null ? null : new ShellError("sharpie::ulimit::cannot_change", $"Can't change the {limit.Description} limit") { Help = reason };

		return reason is null;
	}

	/// <summary>
	/// One resource limit <c>ulimit</c> knows.
	/// </summary>
	/// <param name="Letter">The option letter that picks it.</param>
	/// <param name="Description">Its name in <c>ulimit -a</c>.</param>
	/// <param name="Units">The unit shown next to the name, or <see langword="null" /> for a plain count.</param>
	/// <param name="Factor">How many of the system's units one shown unit is, like 1024 for kbytes of bytes.</param>
	/// <param name="Linux">Its <c>RLIMIT_*</c> number on Linux.</param>
	/// <param name="MacOS">Its <c>RLIMIT_*</c> number on macOS, or <see langword="null" /> when macOS doesn't have it.</param>
	internal sealed record Limit(char Letter, string Description, string? Units, ulong Factor, int? Linux, int? MacOS)
	{
		/// <summary>
		/// Gets the <c>RLIMIT_*</c> number on the OS <see cref="Limits" /> was asked for.
		/// </summary>
		/// <value>
		/// The number, or -1 for the pipe size, which isn't a limit.
		/// </value>
		public int Resource { get; init; }
	}

	/// <summary>
	/// What one <c>ulimit</c> command asks for.
	/// </summary>
	internal sealed class Request
	{
		/// <summary>
		/// Gets or sets a value that indicates whether <c>-H</c> was given.
		/// </summary>
		/// <value>
		/// <see langword="true" /> when it was; otherwise, <see langword="false" />.
		/// </value>
		public bool Hard { get; set; }

		/// <summary>
		/// Gets or sets a value that indicates whether <c>-S</c> was given.
		/// </summary>
		/// <value>
		/// <see langword="true" /> when it was; otherwise, <see langword="false" />.
		/// </value>
		public bool Soft { get; set; }

		/// <summary>
		/// Gets or sets a value that indicates whether <c>-a</c> was given, which prints every limit and ignores the
		/// rest.
		/// </summary>
		/// <value>
		/// <see langword="true" /> when it was; otherwise, <see langword="false" />.
		/// </value>
		public bool All { get; set; }

		/// <summary>
		/// Gets the limits to print or set, in the order given.
		/// </summary>
		/// <value>
		/// Each limit with the value to set it to, or <see langword="null" /> to print it.
		/// </value>
		public List<(Limit Limit, string? Value)> Items { get; } = [];

		/// <summary>
		/// Gets a value that indicates whether printing shows the hard limit.
		/// </summary>
		/// <value>
		/// <see langword="true" /> when only <c>-H</c> was given; otherwise, <see langword="false" /> for the soft limit.
		/// </value>
		public bool ShowHard => this.Hard && !this.Soft;

		/// <summary>
		/// Gets a value that indicates whether setting changes the hard limit.
		/// </summary>
		/// <value>
		/// <see langword="true" /> unless only <c>-S</c> was given.
		/// </value>
		public bool SetHard => this.Hard || !this.Soft;

		/// <summary>
		/// Gets a value that indicates whether setting changes the soft limit.
		/// </summary>
		/// <value>
		/// <see langword="true" /> unless only <c>-H</c> was given.
		/// </value>
		public bool SetSoft => this.Soft || !this.Hard;
	}
}
