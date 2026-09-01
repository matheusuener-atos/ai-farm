using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class ConversationTitleTests
{
    [Fact]
    public void NormalizesWhitespace()
    {
        var title = ConversationTitle.FromPrompt("  Planejar\n\tuma nova   tarefa  ");

        Assert.Equal("Planejar uma nova tarefa", title);
    }

    [Fact]
    public void TruncatesLongPromptsWithEllipsis()
    {
        var title = ConversationTitle.FromPrompt(new string('a', 120));

        Assert.Equal(ConversationTitle.MaximumLength, title.Length);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void RejectsEmptyPrompt()
    {
        Assert.Throws<ArgumentException>(() => ConversationTitle.FromPrompt("   "));
    }
}
