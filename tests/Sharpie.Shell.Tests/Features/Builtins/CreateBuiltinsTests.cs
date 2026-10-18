using FluentAssertions;
using Sharpie.Shell.Common;
using Sharpie.Shell.Features.Builtins;
using Sharpie.Shell.Features.Variables;

namespace Sharpie.Shell.Tests.Features.Builtins;

/// <summary>
/// Tests for <see cref="Program.CreateBuiltins" />.
/// </summary>
[Collection(GlobalState.Name)]
public sealed class CreateBuiltinsTests
{
	/// <summary>
	/// Checks that every builtin has a name and a description, and that no two share a name, since the shell adds them
	/// to one dictionary by name.
	/// </summary>
	[Fact]
	public void CreateBuiltins_EveryBuiltin_HasUniqueNameAndDescription()
	{
		// Act
		var builtins = Program.CreateBuiltins([]);

		// Assert
		_ = builtins.Should().OnlyContain(command => command.Name.Length > 0 && command.Description.Length > 0);
		_ = builtins.Select(command => command.Name).Should().OnlyHaveUniqueItems();
	}

	/// <summary>
	/// Checks that <c>return</c> ends a file that either <c>.</c> or <c>source</c> runs, and is refused once the file
	/// is done.
	/// </summary>
	/// <param name="name">The name the file is run with.</param>
	[Theory]
	[InlineData(".")]
	[InlineData("source")]
	public void Create_ReturnInSourcedFile_EndsFile(string name)
	{
		// Arrange
		var commands = new Dictionary<string, ICommand>();
		var path = Path.GetTempFileName();

		File.WriteAllText(path, "return 5\n");

		foreach (var command in BuiltinCommands.Create(commands, _ => commands["return"].Run(["5"])))
		{
			commands.Add(command.Name, command);
		}

		using var error = new StringWriter();
		var console = Console.Error;

		Console.SetError(error);

		try
		{
			// Act
			var status = commands[name].Run([path]);
			var after = commands["return"].Run(["5"]);

			// Assert
			_ = status.Should().Be(5);
			_ = after.Should().Be(2);
		}
		finally
		{
			Console.SetError(console);
			File.Delete(path);
		}
	}

	/// <summary>
	/// Checks that the script runner is a <c>.</c> of its own, so <c>return</c> at a script's top level is refused
	/// with 2 and the script goes on, as in bash.
	/// </summary>
	[Fact]
	public void CreateScriptRunner_ReturnAtTopLevel_IsRefused()
	{
		// Arrange
		var commands = new Dictionary<string, ICommand>();
		var path = Path.GetTempFileName();
		var statuses = new List<int>();

		File.WriteAllText(path, "return 5\nnext\n");

		foreach (var command in BuiltinCommands.Create(commands, _ => 0))
		{
			commands.Add(command.Name, command);
		}

		var sut = BuiltinCommands.CreateScriptRunner((DeclareCommand)commands["declare"], line =>
		{
			statuses.Add(line == "next" ? 0 : commands["return"].Run(["5"]));

			return statuses[^1];
		});

		using var error = new StringWriter();
		var console = Console.Error;

		Console.SetError(error);

		try
		{
			// Act
			var status = sut.Run([path]);

			// Assert
			_ = sut.Name.Should().Be(".");
			_ = statuses.Should().Equal(2, 0);
			_ = status.Should().Be(0);
		}
		finally
		{
			Console.SetError(console);
			File.Delete(path);
		}
	}
}
