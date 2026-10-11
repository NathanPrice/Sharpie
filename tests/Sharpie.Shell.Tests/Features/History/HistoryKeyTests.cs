using FluentAssertions;
using Sharpie.Shell.Features.History;

namespace Sharpie.Shell.Tests.Features.History;

/// <summary>
/// Tests for <see cref="HistoryKey" />, through the lookup and store steps its secret-store helpers plug into.
/// </summary>
public sealed class HistoryKeyTests
{
	private readonly List<string> stored = [];

	/// <summary>
	/// Checks that a valid stored key is returned as is and nothing new is stored.
	/// </summary>
	[Fact]
	public void LoadOrCreateWithTool_ValidStoredKey_ReturnsIt()
	{
		// Arrange
		var key = Enumerable.Range(0, 32).Select(each => (byte)each).ToArray();

		// Act
		var result = HistoryKey.LoadOrCreateWithTool(allowCreate: true, () => Convert.ToBase64String(key) + "\n", this.Store);

		// Assert
		_ = result.Should().Equal(key);
		_ = this.stored.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a stored key that isn't valid Base64 or isn't 32 bytes gives no key and is never replaced, so the
	/// history it encrypted can still be read once the key is fixed.
	/// </summary>
	/// <param name="value">The stored value.</param>
	[Theory]
	[InlineData("not base64!")]
	[InlineData("AAAAAAAAAAAAAAAAAAAAAA==")]
	public void LoadOrCreateWithTool_BadStoredKey_ReturnsNullWithoutStoring(string value)
	{
		// Act
		var result = HistoryKey.LoadOrCreateWithTool(allowCreate: true, () => value, this.Store);

		// Assert
		_ = result.Should().BeNull();
		_ = this.stored.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a missing key isn't created when creating is off, as when a history file already exists.
	/// </summary>
	[Fact]
	public void LoadOrCreateWithTool_NoKeyAndCreateOff_ReturnsNullWithoutStoring()
	{
		// Act
		var result = HistoryKey.LoadOrCreateWithTool(allowCreate: false, () => null, this.Store);

		// Assert
		_ = result.Should().BeNull();
		_ = this.stored.Should().BeEmpty();
	}

	/// <summary>
	/// Checks that a missing key is created as 32 bytes and stored as the same bytes in Base64.
	/// </summary>
	[Fact]
	public void LoadOrCreateWithTool_NoKeyAndCreateOn_StoresAndReturnsNewKey()
	{
		// Act
		var result = HistoryKey.LoadOrCreateWithTool(allowCreate: true, () => null, this.Store);

		// Assert
		_ = result.Should().HaveCount(32);
		_ = this.stored.Should().Equal(Convert.ToBase64String(result!));
	}

	/// <summary>
	/// Checks that a new key the store refused isn't returned, since nothing could read the history back later.
	/// </summary>
	[Fact]
	public void LoadOrCreateWithTool_StoreFails_ReturnsNull()
	{
		// Act
		var result = HistoryKey.LoadOrCreateWithTool(allowCreate: true, () => null, _ => false);

		// Assert
		_ = result.Should().BeNull();
	}

	/// <summary>
	/// Records a key handed to the store.
	/// </summary>
	/// <param name="key">The Base64 key.</param>
	/// <returns>
	/// Always <see langword="true" />, a store that works.
	/// </returns>
	private bool Store(string key)
	{
		this.stored.Add(key);

		return true;
	}
}
