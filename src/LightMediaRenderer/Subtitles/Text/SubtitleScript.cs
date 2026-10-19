using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using LightMediaRenderer.Contracts;

namespace LightMediaRenderer.Subtitles.Text;

/// <summary>Which regional form of the CJK scripts a piece of subtitle text is written in.</summary>
/// <remarks>
/// Han characters are unified in Unicode, so the same codepoint is drawn with visibly different
/// strokes depending on whether the font is meant for Japan, China, Taiwan or Korea. Nothing
/// in the text itself says which was intended, and a font manager asked for a codepoint alone will
/// answer with whichever face it happens to rank first. Deciding the region up front is therefore the
/// only way to get a subtitle line that looks like it was set in one language.
/// </remarks>
internal enum SubtitleScript
{
    Unknown,
    Japanese,
    Korean,
    SimplifiedChinese,
    TraditionalChinese,
}

/// <summary>
/// Works out the regional script of subtitle text, and which font families suit it.
/// </summary>
/// <remarks>
/// Detection leans on characters that exist in one orthography and not the other: kana and hangul
/// are decisive on their own, and simplified and traditional Han are told apart by the forms that
/// were actually changed by the 1956 reform. The tables hold the exclusive forms among the 1500 most
/// frequent characters, which is far more than a sentence needs and small enough to stay readable --
/// they were derived from OpenCC's <c>STCharacters</c>/<c>TSCharacters</c> mappings, keeping only the
/// characters that appear on one side alone, so an ambiguous form such as <c>云</c> or <c>叶</c>
/// counts as evidence for neither.
///
/// A single line is often too short to decide -- roughly a quarter of the lines in a real script carry
/// no exclusive form at all -- so callers are expected to aggregate a verdict per style over the whole
/// file with <see cref="ScriptVotes"/>. Over hundreds of lines that is essentially never wrong, and,
/// unlike deciding per line, it cannot make the same style change font halfway through a file.
/// </remarks>
internal static class SubtitleScripts
{
    /// <summary>Forms that exist in simplified Chinese and in no other orthography.</summary>
    private static readonly FrozenSet<char> SimplifiedOnly = ToSet(
        "与专业东丝两严丧个临为丽举义乌乐乔习乡书买乱争亚产亲亿仅从仪们众优会伟" +
        "传伤伦体侠侦侧债倾偿儿兰关兴养内写军农冲决况净减凭凯击刘则" +
        "刚创别剑剧办务动励劲劳势区医华协单卖卫却厅历厉压县参双发变号叹吓吗听启" +
        "吴员响团园围国图圆圣场块坚墙壮声处备复够头夺奋奖奥妇妈孙学" +
        "宁宝实审宪宫宽对寻导将尔尽层属岁岛峰币师带帮并庄庆床库应废开异弃张弹强" +
        "归当录彻径忆忧怀态总恋恶惊惯戏战户执扩扫扬扰抢护报担拥择挥" +
        "损换摄摆摇敌数断无旧时显晓暂术机杀杂权条来杨构枪标树样桥梦检楼横欢欧残" +
        "毁毕气汇汉没泪泽测济润渐温湾满潜灭灯灵灾点烟烦烧热爱爷状犹" +
        "独献玛环现电画疗监盖盘矿码础碍礼离秘积称税稳穷竞笔签简类粮紧红约级纪纯" +
        "纳纵纷纸线练组细织终绍经结绕给络绝统继绪续维综绿缓编缘缩网" +
        "罗罚罢群职联胁胆脑脚脱脸舰艺节苏荡荣药莱获营萨蒋蓝虑虚虽补袭装见观规视" +
        "觉触计订认讨让训议讯记讲许论设访证评识诉诊词译试诗诚话该详" +
        "语误说请诸诺读课谁调谈谋谓谢贝负财责败货质贫购贯贴贵贸费资赏赖赛赞赵赶" +
        "趋车转轮软轰轻载较辆辈辑输辞辩边达过运还这进远违连迟迹选遗" +
        "郑释针钟钱铁银销错键镇镜长门闪闭问间闹闻阅队阳阴阵阶际陆陈险随隐难静韩" +
        "页顶项顺须顾顿预领颗题额风飞饭馆马驻骂验骑鱼鲁鲜鸟鸡麦黄齐" +
        "龙");

    /// <summary>Forms that exist in traditional Chinese and in no other orthography.</summary>
    /// <remarks>
    /// Many of these are also ordinary modern Japanese kanji, which is why kana are weighed first:
    /// a Japanese line reads as traditional Chinese if only the Han characters are counted.
    /// </remarks>
    private static readonly FrozenSet<char> TraditionalOnly = ToSet(
        "並亂亞併來俠個們倫偉側偵備傳債傷傾僅儀億儘償優兒內兩凱別則剛創劇劉劍勁" +
        "動務勞勢勵匯區協卻厲參吳員問啓喪喬單嗎嘆噁噹嚇嚴囌國圍園圓" +
        "圖團執堅報場塊壓壯夠夢奧奪奮婦媽孫學宮實寧審寫寬寶將專尋對導層屬峯島師" +
        "帶幣幫幷庫廢廳張強彆彈彔彙徑從復徹惡愛態慣慮慶憂憑憲憶應懷" +
        "戀戰戲戶掃揚換揮損搖搶擁擇擊擔擴擺擾攝敗敵數斷時暫曆曉書會朮東條棄楊業" +
        "榮構槍樂樓標樣樹橋機橫檢權歎歐歡歲歷歸殘殺毀氣決沒沖況淚淨" +
        "減測溫滅滙滿漢漸潛潤澤濟灣災烏無煙煩熱燈燒營燬爭爲爺爾牀牆狀猶獎獨獲獻" +
        "現瑪環產畢畫異當療發盡監盤盪碼礎礙礦禮稅稱積穩穫窮競筆箇節" +
        "簡簽籤糧糰紀約紅納純紙級紛細紹終組結絕絡給統絲經綜綠維網緊緒線緣編緩練" +
        "縣縮縱總織繞繼續罰罵罷羅羣義習聖聞聯聲職聽脅脫腦腳膽臉臨與" +
        "興舉舊艦莊華菸萊葯蓋蔘蔣蕩薩藍藝藥蘇蘭處虛號衆術衛衝補裝複襬襲見規視親" +
        "覺觀觸訂計訊討訓記訪設許訴診証評詞試詩話該詳認語誠誤說誰課" +
        "調談請論諸諾謀謂講謝證識譭譯議護讀變讓讚貝負財貧貨貫責貴買費貼貿資賞賣" +
        "質賴購賽贊趕趙趨跡蹟車軍軟較載輕輛輩輪輯輸轉轟辦辭辯農逕這" +
        "連進運過達違遠遲選遺還邊鄉鄭醫釋針鈡銀銷錄錢錯鍵鍼鍾鎮鏡鐘鐵長門閃閉開" +
        "間閱關陣陰陳陸陽隊階際隨險隱雖雙雜雞離難電靈靜韓響頁頂項順" +
        "須預頓領頭顆題額類顧顯風飛飯養館馬駐騎驗驚體髮鬚鬧魚魯鮮鳥麗麥黃點齊龍");

    /// <summary>
    /// Families to try, in order, when the script's own font is missing or cannot draw a character.
    /// </summary>
    /// <remarks>
    /// Naming the faces outright is what makes this work the same on every platform: the regional
    /// Noto and Source Han builds ship with Linux and the Flatpak runtime, the vendor fonts below them
    /// are the ones Windows, macOS and Android have always had, and every one of them is a family the
    /// font manager can be asked for by name. Handing the codepoint to the platform matcher instead
    /// leaves the choice to whatever the system happens to rank first, which is how a line ends up
    /// half in one country's forms and half in another's.
    /// </remarks>
    private static readonly string[] JapaneseFamilies =
    [
        "Noto Sans CJK JP", "Noto Sans JP", "Source Han Sans JP", "Source Han Sans",
        "Hiragino Sans", "Hiragino Kaku Gothic ProN", "Yu Gothic", "Meiryo", "MS Gothic",
        "IPAexGothic", "TakaoPGothic", "VL Gothic", "Droid Sans Japanese",
    ];

    private static readonly string[] SimplifiedFamilies =
    [
        "Noto Sans CJK SC", "Noto Sans SC", "Source Han Sans SC", "Source Han Sans CN",
        "PingFang SC", "Microsoft YaHei", "SimHei", "SimSun", "Heiti SC",
        "WenQuanYi Zen Hei", "WenQuanYi Micro Hei", "Droid Sans Fallback",
    ];

    private static readonly string[] TraditionalFamilies =
    [
        "Noto Sans CJK TC", "Noto Sans TC", "Source Han Sans TC", "Source Han Sans TW",
        "Noto Sans CJK HK", "Noto Sans HK", "PingFang TC", "Microsoft JhengHei",
        "PMingLiU", "MingLiU", "Heiti TC", "WenQuanYi Zen Hei", "Droid Sans Fallback",
    ];

    private static readonly string[] KoreanFamilies =
    [
        "Noto Sans CJK KR", "Noto Sans KR", "Source Han Sans KR", "Source Han Sans K",
        "Apple SD Gothic Neo", "Malgun Gothic", "Gulim", "Batang", "NanumGothic",
    ];

    private static readonly string[] NoFamilies = [];

    /// <summary>Families tried for text that is not CJK, or whose region could not be worked out.</summary>
    public static readonly string[] LatinFamilies =
        ["Arial", "Liberation Sans", "DejaVu Sans", "Noto Sans", "Helvetica", "sans-serif"];

    /// <summary>
    /// Maps an ASS <c>Encoding</c> field, or a <c>\fe</c> tag, to the script it selected under GDI.
    /// </summary>
    /// <remarks>
    /// Only the four CJK charsets say anything useful. <c>0</c> (ANSI) and <c>1</c> (default) are what
    /// most scripts carry whatever language they are in, so they are deliberately not an answer.
    /// </remarks>
    public static SubtitleScript FromCharset(int charset) => charset switch
    {
        128 => SubtitleScript.Japanese,          // SHIFTJIS
        129 => SubtitleScript.Korean,            // HANGEUL
        130 => SubtitleScript.Korean,            // JOHAB
        134 => SubtitleScript.SimplifiedChinese, // GB2312
        136 => SubtitleScript.TraditionalChinese,// CHINESEBIG5
        _ => SubtitleScript.Unknown,
    };

    /// <summary>
    /// Reads the region a font name advertises, as fansub scripts almost always spell it out.
    /// </summary>
    /// <remarks>
    /// Only unmistakable markers count. Two-letter tags such as <c>SC</c> are matched as whole words
    /// so that they cannot fire inside an unrelated name, and the Adobe and Morisawa suffixes
    /// (<c>Pro</c>, <c>ProN</c>, <c>Pr6N</c>, <c>Std</c>, <c>StdN</c>) only ever appear on Japanese
    /// families.
    /// </remarks>
    public static SubtitleScript FromFamilyName(string? family)
    {
        if (string.IsNullOrEmpty(family))
            return SubtitleScript.Unknown;

        if (Contains(family, "GBK") || Contains(family, "GB2312") || Contains(family, "GB18030")
            || Contains(family, "Hans") || Contains(family, "简"))
            return SubtitleScript.SimplifiedChinese;

        if (Contains(family, "BIG5") || Contains(family, "Hant") || Contains(family, "繁")
            || Contains(family, "MingLiU") || Contains(family, "JhengHei"))
            return SubtitleScript.TraditionalChinese;

        if (Contains(family, "Mincho") || Contains(family, "Meiryo") || Contains(family, "Kaku"))
            return SubtitleScript.Japanese;

        foreach (Range word in Words(family))
        {
            ReadOnlySpan<char> tag = family.AsSpan(word);

            if (Is(tag, "SC") || Is(tag, "CN") || Is(tag, "GB"))
                return SubtitleScript.SimplifiedChinese;
            if (Is(tag, "TC") || Is(tag, "TW") || Is(tag, "HK") || Is(tag, "B5"))
                return SubtitleScript.TraditionalChinese;
            if (Is(tag, "JP") || Is(tag, "JIS") || Is(tag, "Pro") || Is(tag, "ProN")
                || Is(tag, "Pr6") || Is(tag, "Pr6N") || Is(tag, "Std") || Is(tag, "StdN"))
                return SubtitleScript.Japanese;
            if (Is(tag, "KR") || Is(tag, "KO"))
                return SubtitleScript.Korean;
        }

        return SubtitleScript.Unknown;
    }

    /// <summary>
    /// Reads the script of one event's text, skipping the override blocks.
    /// </summary>
    /// <remarks>
    /// Kana and hangul settle it outright, because neither ever appears in Chinese -- except for the
    /// lone <c>の</c> that Chinese titles borrow, which is why one kana only counts when there is no
    /// Han evidence at all to weigh it against.
    /// </remarks>
    public static SubtitleScript Detect(ReadOnlySpan<char> text)
    {
        int kana = 0, hangul = 0, simplified = 0, traditional = 0;
        bool inTag = false;

        foreach (char c in text)
        {
            if (c == '{')
                inTag = true;
            else if (c == '}')
                inTag = false;
            else if (inTag)
                continue;
            else if (IsKana(c))
                kana++;
            else if (IsHangul(c))
                hangul++;
            else if (SimplifiedOnly.Contains(c))
                simplified++;
            else if (TraditionalOnly.Contains(c))
                traditional++;
        }

        int han = simplified + traditional;
        if (kana >= 2 || (kana == 1 && han == 0))
            return SubtitleScript.Japanese;
        if (hangul >= 2 || (hangul == 1 && han == 0))
            return SubtitleScript.Korean;
        if (simplified > traditional)
            return SubtitleScript.SimplifiedChinese;
        if (traditional > simplified)
            return SubtitleScript.TraditionalChinese;

        return SubtitleScript.Unknown;
    }

    public static string[] PreferredFamilies(SubtitleScript script) => script switch
    {
        SubtitleScript.Japanese => JapaneseFamilies,
        SubtitleScript.Korean => KoreanFamilies,
        SubtitleScript.SimplifiedChinese => SimplifiedFamilies,
        SubtitleScript.TraditionalChinese => TraditionalFamilies,
        _ => NoFamilies,
    };

    /// <summary>The tag to hand a font manager that does take a language hint.</summary>
    public static string? Bcp47(SubtitleScript script) => script switch
    {
        SubtitleScript.Japanese => "ja-JP",
        SubtitleScript.Korean => "ko-KR",
        SubtitleScript.SimplifiedChinese => "zh-Hans",
        SubtitleScript.TraditionalChinese => "zh-Hant",
        _ => null,
    };

    private static bool IsKana(char c)
        // Excludes the prolonged sound mark and the middle dot, which Chinese also uses.
        => (c is >= '\u3041' and <= '\u3096')
            || (c is >= '\u30A1' and <= '\u30FA')
            || (c is >= '\uFF66' and <= '\uFF9D');

    private static bool IsHangul(char c)
        => (c is >= '\uAC00' and <= '\uD7A3')
            || (c is >= '\u1100' and <= '\u11FF')
            || (c is >= '\u3131' and <= '\u318E');

    private static bool Contains(string family, string marker)
        => family.Contains(marker, StringComparison.OrdinalIgnoreCase);

    private static bool Is(ReadOnlySpan<char> word, string tag)
        => word.Equals(tag, StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits a family name on everything that is not a letter or a digit.</summary>
    private static List<Range> Words(string family)
    {
        List<Range> words = [];
        int start = -1;
        for (int i = 0; i <= family.Length; i++)
        {
            bool part = i < family.Length && char.IsLetterOrDigit(family[i]);
            if (part && start < 0)
                start = i;
            else if (!part && start >= 0)
            {
                words.Add(new Range(start, i));
                start = -1;
            }
        }

        return words;
    }

    private static FrozenSet<char> ToSet(string characters)
    {
        HashSet<char> set = [];
        foreach (char c in characters)
            set.Add(c);
        return set.ToFrozenSet();
    }
}

/// <summary>
/// Counts the per-line verdicts of a group of events so that the group as a whole can be classified.
/// </summary>
internal struct ScriptVotes
{
    private int japanese;
    private int korean;
    private int simplified;
    private int traditional;

    public void Add(SubtitleScript script)
    {
        switch (script)
        {
            case SubtitleScript.Japanese: japanese++; break;
            case SubtitleScript.Korean: korean++; break;
            case SubtitleScript.SimplifiedChinese: simplified++; break;
            case SubtitleScript.TraditionalChinese: traditional++; break;
        }
    }

    /// <summary>The script most of the lines were written in, or <c>Unknown</c> if none were CJK.</summary>
    public readonly SubtitleScript Winner
    {
        get
        {
            SubtitleScript winner = SubtitleScript.Unknown;
            int best = 0;

            if (japanese > best)
                (winner, best) = (SubtitleScript.Japanese, japanese);
            if (korean > best)
                (winner, best) = (SubtitleScript.Korean, korean);
            if (simplified > best)
                (winner, best) = (SubtitleScript.SimplifiedChinese, simplified);
            if (traditional > best)
                (winner, best) = (SubtitleScript.TraditionalChinese, traditional);

            return winner;
        }
    }
}
