using Aml.Core.Explanation;

namespace Aml.Tests.Unit.Screening;

/// <summary>Identifiers written next to a person's name in made-up news and judgment lines (watchlist-and-adverse-information.md#news-digest).</summary>
public class NewsIdentifiersTests
{
    [Theory]
    [InlineData("被告王大明（男，民國70年1月1日生，身分證統一編號：A123456789號，住臺中市西區民生路1號）涉嫌詐欺。", "王大明",
        "GENDER:男|BIRTH_DATE:民國70年1月1日生|ID_NUMBER:A12*****89|RESIDENCE:住臺中市西區民生路1號")]
    [InlineData("臺中市45歲男子王大明涉嫌詐欺。", "王大明", "GENDER:男|AGE:45歲|RESIDENCE:臺中市")]
    [InlineData("越南籍阮文甲遭起訴。", "阮文甲", "NATIONALITY:越南籍")]
    [InlineData("司機王大明遭逮捕。", "王大明", "OCCUPATION:司機")]
    [InlineData("王大明檢察官表示，全案偵辦中。", "王大明", "OCCUPATION:檢察官")]
    [InlineData("法　官　王大明", "王大明", "OCCUPATION:法官")]
    [InlineData("王大明，男，45歲，涉嫌詐欺。", "王大明", "GENDER:男|AGE:45歲")]
    [InlineData("被告 王大明 男 45歲（民國70年生）", "王大明", "GENDER:男|AGE:45歲")]
    [InlineData("王大明今年45歲。", "王大明", "AGE:45歲")]
    [InlineData("王姓男子遭起訴。", "王姓男子", "GENDER:男")]
    [InlineData("被告王大明自陳高職畢業、從事水電工作。", "王大明", "OCCUPATION:水電")]
    [InlineData("被告王大明自陳高職畢業，從事水電工作。", "王大明", "")]
    [InlineData("被告王大明擔任車手，提領款項。", "王大明", "")]
    [InlineData("被告王大明從事虛擬貨幣買賣。", "王大明", "")]
    [InlineData("未滿18歲王小明遭查獲。", "王小明", "")]
    [InlineData("王大明（化名）報案。", "王大明", "")]
    [InlineData("王大明涉嫌詐欺。", "王大明", "")]
    [InlineData("王大明（A123456789 某某公司職員，男）", "王大明", "GENDER:男")]
    [InlineData("王大明（某某公司負責人）", "王大明", "EMPLOYER:某某公司負責人")]
    [InlineData("王大明（涉嫌詐欺某某銀行款項）", "王大明", "")]
    [InlineData("王大明，昨天前往臺北市，警方到場。", "王大明", "")]
    [InlineData("王大明（住院治療）", "王大明", "")]
    [InlineData("被告王大明與李小華共犯，李小華現擔任店員。", "王大明", "")]
    [InlineData("王大明之妻擔任教師。", "王大明", "")]
    public void Reads_what_is_written_next_to_the_name(string text, string form, string expected)
    {
        var found = NewsIdentifiers.Read(text, [form]).Select(i => $"{Code(i.Kind)}:{i.Text ?? i.Number}");

        Assert.Equal(expected, string.Join("|", found));
    }

    [Fact]
    public void A_one_character_form_is_not_read()
    {
        Assert.Empty(NewsIdentifiers.Read("王（男，45歲）", ["王"]));
    }

    private static string Code(NewsIdentifierKind k) => k switch
    {
        NewsIdentifierKind.BirthDate => "BIRTH_DATE",
        NewsIdentifierKind.IdNumber => "ID_NUMBER",
        _ => k.ToString().ToUpperInvariant(),
    };
}
