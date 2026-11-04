namespace Aml.Core.Codes;

/// <summary>One code: what it is called, what it means, the rule that emits it and the document section that defines it.</summary>
public sealed record CodeEntry(string Code, string Kind, string Name, string Description, string Rule, string Doc);

/// <summary>
/// Every code the system emits, the single source of their meaning (GET /api/codes). A value with detail attached is looked up
/// by its key (<see cref="Key"/>); values joined with "；" are looked up one by one.
/// </summary>
public static class CodeCatalog
{
    public const string Version = "codes-2";

    private const string Identity = "docs/feature/identity-adjudication.md";
    private const string Extraction = "docs/feature/extraction-and-normalization.md";
    private const string Watchlist = "docs/feature/watchlist-and-adverse-information.md";
    private const string Disposition = "docs/feature/review-and-disposition.md";
    private const string Api = "docs/feature/pipeline-api-and-batch.md";

    /// <summary>Catalog key of an emitted value: SOFT_DIFFERENCE（Jaccard …） → SOFT_DIFFERENCE, ENCODING_FALLBACK:a.txt → ENCODING_FALLBACK.</summary>
    public static string Key(string value) => value.Split('（', ':')[0].Trim();

    public static CodeEntry? Find(string value) => All.GetValueOrDefault(Key(value));

    public static readonly IReadOnlyDictionary<string, CodeEntry> All = new (string Kind, string Code, string Name, string Description, string Rule, string Doc)[]
    {
        // Identity decision (stage 1 and stage 3).
        ("IDENTITY_REASON", "TEMPORALLY_PLAUSIBLE", "時序合理", "受檢對象在報導事件或職務起始時的年齡可能成立；不是身分佐證。", "EvidenceBuilder.TemporalEvidence", Identity + "#temporal-and-role-consistency"),
        ("IDENTITY_REASON", "IMPOSSIBLE_AGE_AT_REPORTED_EVENT", "事件時年齡不可能", "受檢對象在名單記載的事件時不可能已達成年；已由 GC-01 驗證的硬衝突，可排除。", "HardConflictGate.Check", Identity + "#hard-conflict-gate"),
        ("IDENTITY_REASON", "IMPOSSIBLE_ROLE_AT_REPORTED_EVENT", "事件時職務不可能", "受檢對象在職務起始時未達該職務的法定年齡；規則待驗證，維持 REVIEW。", "HardConflictGate.Check", Identity + "#hard-conflict-gate"),
        ("IDENTITY_REASON", "FULL_DOB_CONFLICT", "完整出生日期不符", "雙方都有完整出生日期且不同；第三階段第 1 列，REVIEW。", "EvidenceBuilder.Build", Identity + "#stage-3-table"),
        ("IDENTITY_REASON", "DOCUMENT_IDENTITY_CONFLICT", "同類證件不符", "同類型、同發證國的證件號碼不同；第三階段第 1 列，REVIEW。", "EvidenceBuilder.DocumentEvidence", Identity + "#stage-3-table"),
        ("IDENTITY_REASON", "DEATH_BEFORE_CUSTOMER_BIRTH", "名單人物死於受檢對象出生前", "名單記載的死亡日期早於受檢對象出生；規則待驗證，維持 REVIEW。", "HardConflictGate.Check", Identity + "#hard-conflict-gate"),
        ("IDENTITY_REASON", "DOB_CONFLICT", "出生日期不符", "名單所有出生日期都與受檢對象不符，且雙方沒有共同的完整日期；第三階段第 1 列。也用於拒絕出生日期矛盾的名單合併。", "EvidenceBuilder.Build", Identity + "#stage-3-table"),
        ("IDENTITY_REASON", "BIRTH_YEAR_MATCH_FULL_DATE_NOT_COMPARABLE", "出生年相符，完整日期無法比對", "出生年相同，但至少一方只有年份。", "EvidenceBuilder.Build", Identity + "#reason-code-registry"),
        ("IDENTITY_REASON", "GENDER_CONFLICT", "性別不符", "雙方性別不同；前提條件衝突，排除第三階段第 3、4 列。也用於拒絕性別矛盾的名單合併。", "EvidenceBuilder.Build", Identity + "#stage-3-table"),
        ("IDENTITY_REASON", "SOURCE_QUALITY_DOWNGRADE", "來源品質降級", "已驗證的硬衝突因來源品質疑慮而維持 REVIEW。", "HardConflictGate.SourceQualityConcern", Identity + "#hard-conflict-gate"),
        ("IDENTITY_REASON", "NON_PERSON_SUBJECT_REVIEW", "非自然人記錄待複核", "名單是組織、船舶等非自然人記錄，沒有可比對的實體識別碼，或受檢對象是自然人；REVIEW。", "IdentityDecisionPolicy.EntityDecision", Identity + "#reason-code-registry"),
        ("IDENTITY_REASON", "NO_BILATERAL_DIRECT_IDENTIFIER", "雙方沒有共同直接識別資料", "雙方沒有同時具備完整出生日期或可比對的證件。", "IdentityDecisionPolicy.Decide", Identity + "#hit-prerequisites-and-direct-identifiers"),
        ("IDENTITY_REASON", "UNIQUE_IDENTIFIER_AGREEMENT", "唯一識別碼相符", "同類型、同發證國的證件號碼相同；第三階段第 2 列，PROBABLE_MATCH，對外仍為 REVIEW。", "IdentityDecisionPolicy.Stage3", Identity + "#stage-3-table"),
        ("IDENTITY_REASON", "STRONG_SUPPORT_COMBINATION", "強佐證組合", "完整出生日期相符，另有至少一項非前提佐證；第三階段第 3 列。", "IdentityDecisionPolicy.Stage3", Identity + "#stage-3-table"),
        ("IDENTITY_REASON", "CONSISTENT_AT_AVAILABLE_PRECISION", "在可得精度內一致", "前提條件相符，出生日期在資料可得精度內相符，證件與職業沒有未決差異；第三階段第 4 列。", "IdentityDecisionPolicy.Stage3", Identity + "#stage-3-table"),
        ("IDENTITY_REASON", "PARTIAL_SUPPORT_ONLY", "僅部分佐證", "有佐證，但未達 PROBABLE_MATCH 的組合；第三階段第 5 列。", "IdentityDecisionPolicy.Stage3", Identity + "#stage-3-table"),
        ("IDENTITY_REASON", "NO_NON_PREREQUISITE_SUPPORT", "僅前提條件相符", "除姓名、國籍、性別等前提條件外沒有任何佐證；第三階段第 6 列。", "IdentityDecisionPolicy.Stage3", Identity + "#stage-3-table"),
        ("IDENTITY_REASON", "AGE_COMPATIBLE_AT_REPORT", "報導年齡相容", "新聞所述年齡與推算年齡相差在 2 歲內；不是佐證。", "StatedAgeCheck.Check", Identity + "#age-stated-in-a-source"),
        ("IDENTITY_REASON", "AGE_DIFFERENCE_AT_REPORT", "報導年齡不符", "新聞所述年齡與推算年齡相差超過 2 歲；軟衝突，第三階段第 1 列。", "StatedAgeCheck.Check", Identity + "#age-stated-in-a-source"),
        ("IDENTITY_REASON", "AGE_NOT_COMPARABLE", "報導年齡無法比對", "新聞有年齡，但缺完整出生日期或報導日期，或年齡是事件當時（時年）。", "StatedAgeCheck.Check", Identity + "#age-stated-in-a-source"),
        ("IDENTITY_REASON", "ENTITY_IDENTIFIER_AGREEMENT", "實體識別碼相符", "非自然人記錄：雙方登記號碼或 IMO 號碼相同。", "IdentityDecisionPolicy.EntityDecision", Identity + "#reason-code-registry"),
        ("IDENTITY_REASON", "ENTITY_IDENTIFIER_CONFLICT", "實體識別碼不符", "非自然人記錄：同一種識別碼號碼不同。", "IdentityDecisionPolicy.EntityDecision", Identity + "#reason-code-registry"),

        // Pair result.
        ("PAIR_REASON", "RELEASE_CONTROL_HUMAN_REVIEW", "放行控制：人工複核", "身分 PROBABLE_MATCH 在第一階段一律以 REVIEW 對外，交由人工。", "AssessmentPolicy.Assess", Disposition + "#two-results-read-separately"),
        ("PAIR_REASON", "SOURCE_CONDITION_UNKNOWN", "名單條件未知", "名單條件無法確定（UNKNOWN），結果維持 REVIEW。", "AssessmentPolicy.Assess", Watchlist + "#reason-codes"),

        // Article 22 disposition recommendation and its pending checks.
        ("DISPOSITION", "ART22_FOREIGN_PEP_PRESUMPTION", "第 22 條①：外國 PEP 推定", "依第 22 條第 1 款推定；身分判斷不變。", "DispositionPolicy.Recommend", Disposition + "#article-22-1-foreign-pep-presumption"),
        ("DISPOSITION", "ART22_ADVERSE_UNRELATED", "第 22 條②：負面新聞與洗錢無關", "依第 22 條第 2 款，ADVERSE_MEDIA 條件不採計。", "DispositionPolicy.Recommend", Disposition + "#article-22-2-adverse-news-unrelated-to-mltfpf"),
        ("DISPOSITION", "ART22_IDENTITY_NO_MATCH", "第 22 條：身分不符", "預設：身分 NO_MATCH，建議 NOT_ON_LIST。", "DispositionPolicy.Recommend", Disposition + "#default"),
        ("DISPOSITION", "ART22_NOT_DETERMINED", "第 22 條③：無法判定", "依第 22 條第 3 款不給建議，列出待查核事項。", "DispositionPolicy.Recommend", Disposition + "#article-22-3-cannot-be-determined"),
        ("DISPOSITION", "ART22_RULING_20261014_MAY_APPLY", "2026-10-14 函釋可能適用", "標示函釋可能適用；系統不套用。", "DispositionPolicy.Recommend", Disposition + "#direct-identifiers-and-the-2026-10-14-ruling"),
        ("PENDING_CHECK", "CONFIRM_NATIONALITY", "確認國籍", "需確認受檢對象國籍。", "DispositionPolicy.Recommend", Disposition + "#verification-records"),
        ("PENDING_CHECK", "EMPLOYMENT_RELEVANCE", "職業關聯", "需確認職業或雇主與名單內容的關聯。", "DispositionPolicy.Recommend", Disposition + "#verification-records"),
        ("PENDING_CHECK", "REGION_RELEVANCE", "地區關聯", "需確認地區與名單內容的關聯。", "DispositionPolicy.Recommend", Disposition + "#verification-records"),
        ("PENDING_CHECK", "IDENTITY_CHECK", "身分查核", "需以其他資料查核是否為同一人。", "DispositionPolicy.Recommend", Disposition + "#verification-records"),
        ("PENDING_CHECK", "JUDGMENT_PARTY_IDENTITY", "判決當事人身分", "需查核判決書當事人是否為受檢對象。", "DispositionPolicy.Recommend", Disposition + "#verification-records"),
        ("PENDING_CHECK", "FORMER_PEP_INFLUENCE", "前 PEP 影響力", "需評估卸任 PEP 是否仍有影響力。", "DispositionPolicy.Recommend", Disposition + "#verification-records"),

        // Source conditions.
        ("CONDITION_REASON", "FLAG_UNPARSEABLE", "旗標無法讀取", "名單旗標值無法讀取，條件為 UNKNOWN。", "SourceConditions.Determine", Watchlist + "#reason-codes"),
        ("CONDITION_REASON", "FLAG_CATEGORY_CONFLICT", "旗標與類別矛盾", "旗標與名單類別矛盾；以旗標為準並記錄。", "SourceConditions.Determine", Watchlist + "#reason-codes"),
        ("CONDITION_REASON", "BLACKLIST_TYPE_UNDECIDED", "黑名單類型未定", "黑名單＝YES，但來源欄位無法決定名單類型。", "SourceConditions.Determine", Watchlist + "#reason-codes"),
        ("CONDITION_REASON", "IS_RCA_FLAG", "RCA 旗標", "RCA 條件來自 Is RCA? = YES。", "SourceConditions.Determine", Watchlist + "#reason-codes"),
        ("CONDITION_REASON", "UNMAPPED_CATEGORY", "未對應的類別", "名單類別沒有對應的名單類型；UNKNOWN，待複核。", "SourceConditions.Determine", Watchlist + "#reason-codes"),
        ("CONDITION_REASON", "SANCTION_MEASURE_NOT_CONFIRMED", "制裁措施未確認", "制裁名單未確認現行措施；條件為 UNKNOWN。", "SourceConditions.Determine", "docs/feature/sanctions-screening.md#sanction-condition"),
        ("CONDITION_REASON", "RCA_OUTSIDE_STATUTORY_SCOPE", "RCA 超出法定範圍", "關係不在法定 RCA 範圍內。", "SourceConditions.Determine", "docs/feature/pep-rca-screening.md#rca-relationship-scope"),
        ("PEP_TYPE", "PEP_FAMILY", "PEP 家屬", "名單類別表明為 PEP 的家庭成員。", "SourceConditions.PepType", "docs/feature/pep-rca-screening.md#pep-source-attributes"),
        ("PEP_TYPE", "PEP_CLOSE_ASSOCIATE", "PEP 密切關係人", "名單類別表明為 PEP 的密切關係人。", "SourceConditions.PepType", "docs/feature/pep-rca-screening.md#pep-source-attributes"),
        ("PEP_TYPE", "RCA_UNCONFIRMED", "RCA 關係未確認", "RCA 名單，但關係類型未說明。", "SourceConditions.PepType", "docs/feature/pep-rca-screening.md#rca-relationship-scope"),
        ("PEP_TYPE", "FORMER_PEP", "卸任 PEP", "所有記錄的職務都已結束。", "SourceConditions.PepType", "docs/feature/pep-rca-screening.md#former-pep"),

        // Risk categories of adverse information.
        ("RISK_CATEGORY", "FINANCIAL_CRIME", "金融犯罪", "名單或新聞的風險類別：金融犯罪。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "TAX_CRIME", "稅務犯罪", "名單或新聞的風險類別：稅務犯罪。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "TAX_EVASION", "逃漏稅", "名單或新聞的風險類別：逃漏稅。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "TRAFFICKING", "販運", "名單或新聞的風險類別：販運。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "HUMAN_TRAFFICKING", "人口販運", "名單或新聞的風險類別：人口販運。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "DRUG_TRAFFICKING", "毒品販運", "名單或新聞的風險類別：毒品販運。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "ORGANISED_CRIME", "組織犯罪", "名單或新聞的風險類別：組織犯罪。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "TERRORISM", "恐怖主義", "名單或新聞的風險類別：恐怖主義。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "CORRUPTION", "貪污", "名單或新聞的風險類別：貪污。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "BRIBERY", "賄賂", "名單或新聞的風險類別：賄賂。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "MONEY_LAUNDERING", "洗錢", "名單或新聞的風險類別：洗錢。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "FRAUD", "詐欺", "名單或新聞的風險類別：詐欺。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "WAR_CRIMES", "戰爭罪", "名單或新聞的風險類別：戰爭罪。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "CYBERCRIME", "網路犯罪", "名單或新聞的風險類別：網路犯罪。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "ENVIRONMENTAL_CRIME", "環境犯罪", "名單或新聞的風險類別：環境犯罪。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "CORPORATE_CRIME", "企業犯罪", "名單或新聞的風險類別：企業犯罪。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "FORGERY", "偽造", "名單或新聞的風險類別：偽造。", "SourceConditions", Watchlist + "#risk-categories"),
        ("RISK_CATEGORY", "BREACH_OF_TRUST", "背信", "名單或新聞的風險類別：背信。", "SourceConditions", Watchlist + "#risk-categories"),

        // Evidence details: explain an evidence status, never change a row on their own.
        ("EVIDENCE_DETAIL", "NOT_COMPARABLE", "無法比對", "雙方都有值但無法比對（不同證件類型或發證國；只有職業類別）；不阻擋第 4 列。", "EvidenceBuilder", Identity + "#reason-code-registry"),
        ("EVIDENCE_DETAIL", "NOT_A_DOCUMENT", "非身分證件", "參考號碼不是身分證件，不作證件比對。", "ExplanationBuilder.Comparison", Identity + "#evidence-groups"),
        ("EVIDENCE_DETAIL", "SAME_IDENTIFIER_NATURE_UNCONFIRMED", "相同號碼，性質未確認", "雙方有相同的非證件識別碼；性質未確認，僅 LOW 佐證。", "EvidenceBuilder.OtherIdentifierEvidence", Identity + "#evidence-groups"),
        ("EVIDENCE_DETAIL", "DIFFERENT_ORGANIZATION", "不同機構", "雙方任職機構不同；不是衝突。", "EvidenceBuilder.EmploymentEvidence", Identity + "#comparison-rules"),
        ("EVIDENCE_DETAIL", "SAME_ORGANIZATION_SAME_PERIOD", "同機構同期間", "雙方在重疊期間任職同一機構；MEDIUM 佐證。", "EvidenceBuilder.EmploymentEvidence", Identity + "#comparison-rules"),
        ("EVIDENCE_DETAIL", "SAME_ORGANIZATION_NO_COMMON_PERIOD", "同機構，無共同期間", "同一機構，但缺期間或期間不重疊；不確定。", "EvidenceBuilder.EmploymentEvidence", Identity + "#comparison-rules"),
        ("EVIDENCE_DETAIL", "SAME_NAMED_RELATIVE", "同名關係人", "雙方記載同名且同關係的關係人（冒號後為關係）；佐證。", "EvidenceBuilder.RelativeEvidence", Identity + "#evidence-groups"),
        ("EVIDENCE_DETAIL", "SOFT_DIFFERENCE", "地址軟差異", "地址差異只是弱訊號，從不構成衝突；括號內為 Jaccard 相似度。", "EvidenceBuilder.AddressEvidence", Identity + "#comparison-rules"),
        ("EVIDENCE_DETAIL", "SAME_ADDRESS_NO_PERIOD", "相同地址，無期間", "地址結構鍵相同，但雙方都沒有居住期間；不是佐證。", "EvidenceBuilder.AddressEvidence", Identity + "#comparison-rules"),
        ("EVIDENCE_DETAIL", "NOT_IDENTICAL_AFTER_NORMALIZATION", "正規化後仍不同", "姓名正規化後不完全相同；括號內為相似度。", "Normalize.CompareNames", Identity + "#name-comparison"),
        ("EVIDENCE_DETAIL", "NAME_ORDER_VARIANT", "姓名順序不同", "姓氏位置移動後姓名相同。", "Normalize.CompareNames", Identity + "#name-comparison"),
        ("EVIDENCE_DETAIL", "DIACRITIC_VARIANT", "變音符號差異", "去除變音符號後姓名相同。", "Normalize.CompareNames", Identity + "#name-comparison"),
        ("EVIDENCE_DETAIL", "NOT_COMPARABLE_CROSS_SCRIPT", "跨文字無法比對", "雙方姓名沒有共同的文字系統。", "Normalize.CompareNames", Identity + "#name-comparison"),
        ("EVIDENCE_DETAIL", "VARIANT_CHARACTERS", "異體字", "受控異體字轉換後姓名相同。", "Normalize.CompareNames", Identity + "#name-comparison"),
        ("EVIDENCE_DETAIL", "ROMANIZATION_VARIANT", "拼音系統差異", "羅馬拼音在不同拼音系統下讀音相同。", "Normalize.CompareNames", Identity + "#name-comparison"),
        ("EVIDENCE_DETAIL", "PROFILE_MULTIPLE_DOB", "名單多個出生日期", "相符的出生日期只是名單眾多或分散的值之一；不是佐證。", "EvidenceBuilder.Build", Identity + "#reason-code-registry"),

        // Data quality findings (extraction).
        ("DATA_QUALITY", "HISTORICAL_ADMIN_DIVISION", "舊行政區劃", "地址使用已改制的行政區名稱。", "TxtCaseReader", Extraction + "#taiwan-address-normalization"),
        ("DATA_QUALITY", "POSTAL_CODE_ONLY", "僅有郵遞區號", "地址欄只有郵遞區號。", "TxtCaseReader", Extraction + "#taiwan-address-normalization"),
        ("DATA_QUALITY", "DUPLICATED_ADDRESS_LINE", "地址重複", "同一地址行重複出現。", "TxtCaseReader", Extraction + "#quality-findings"),
        ("DATA_QUALITY", "UNSUPPORTED_ATTACHMENT", "不支援的附件", "附件格式不支援，未讀取。", "TxtCaseReader", Extraction + "#quality-findings"),
        ("DATA_QUALITY", "LEGACY_RECORD_BOUNDARY_UNCLEAR", "舊處理紀錄邊界不清", "舊系統處理紀錄的分段無法確定。", "TxtCaseReader", Extraction + "#legacy-review-records"),
        ("DATA_QUALITY", "UNPARSEABLE", "無法解析", "表格列或標題無法讀取（冒號後為位置）。", "TxtCaseReader", Extraction + "#quality-findings"),
        ("DATA_QUALITY", "FIELD_CONFLICT", "欄位矛盾", "國家欄位與地址內容矛盾。", "TxtCaseReader", Extraction + "#quality-findings"),
        ("DATA_QUALITY", "AMBIGUOUS", "值不明確", "國家值無法唯一對應到國家，不參與比對（冒號後為欄位）。", "TxtCaseReader", Extraction + "#country-roles"),
        ("DATA_QUALITY", "ENCODING_FALLBACK", "編碼改用 CP950", "檔案不是 UTF-8，改以 CP950（Big5）讀取（冒號後為檔名）。", "TxtCaseReader.Decode", Extraction + "#encoding"),
        ("DATA_QUALITY", "FIELD_ABSENT", "欄位不存在", "身分比對用的欄位不在檔案中（冒號後為欄位）。", "TxtCaseReader", Extraction + "#missing-values"),
        ("DATA_QUALITY", "FIELD_BLANK", "欄位空白", "身分比對用的欄位空白（冒號後為欄位）。", "TxtCaseReader", Extraction + "#missing-values"),
        ("DATA_QUALITY", "PLACEHOLDER_DASH", "欄位為破折號", "身分比對用的欄位只有破折號（冒號後為欄位）。", "TxtCaseReader", Extraction + "#missing-values"),
        ("DATA_QUALITY", "PLACEHOLDER_TEXT", "欄位為佔位文字", "身分比對用的欄位只有「無」等佔位文字（冒號後為欄位）。", "TxtCaseReader", Extraction + "#missing-values"),
        ("DATA_QUALITY", "MALFORMED", "格式錯誤", "值的格式不正確，視為不明確。", "TxtCaseReader", Extraction + "#quality-findings"),

        // What a source was used for.
        ("SOURCE_USE", "PROFILE_RECORD", "名單記錄來源", "名單本身引用的來源。", "ExplanationBuilder", Api + "#explanation-contents"),
        ("SOURCE_USE", "LEGAL_STAGE", "決定法律階段", "附加新聞決定了 ADVERSE_MEDIA 條件的法律階段。", "ExplanationBuilder", Api + "#explanation-contents"),
        ("SOURCE_USE", "CONTEXT", "背景資料", "附加新聞僅作為背景。", "ExplanationBuilder", Api + "#explanation-contents"),

        // Comparison status of one evidence group or comparison row.
        ("COMPARISON_STATUS", "EXACT", "相符", "雙方值相同。", "EvidenceBuilder", Identity + "#comparison-rules"),
        ("COMPARISON_STATUS", "PARTIAL", "部分相符", "在較粗的精度上相符，或部分相同。", "EvidenceBuilder", Identity + "#comparison-rules"),
        ("COMPARISON_STATUS", "CONFLICT", "衝突", "雙方值互相矛盾。", "EvidenceBuilder", Identity + "#comparison-rules"),
        ("COMPARISON_STATUS", "UNCERTAIN", "不確定", "雙方都有值，但無法判斷是否相同。", "EvidenceBuilder", Identity + "#comparison-rules"),
        ("COMPARISON_STATUS", "SUBJECT_MISSING", "受檢對象缺值", "受檢對象沒有此項資料。", "EvidenceBuilder", Identity + "#comparison-rules"),
        ("COMPARISON_STATUS", "PROFILE_MISSING", "名單缺值", "名單沒有此項資料。", "EvidenceBuilder", Identity + "#comparison-rules"),

        // Profile merges.
        ("MERGE", "SHARED_DOCUMENT_KEY", "共同證件鍵", "兩筆名單有相同的可比對證件，合併為一組。", "ProfileMerge", Identity + "#profile-level-deduplication"),
        ("MERGE", "RECORDED_SOURCE_LINK", "來源記錄的連結", "來源明載兩筆名單為同一人，合併為一組。", "ProfileMerge", Identity + "#profile-level-deduplication"),
        ("MERGE", "OVERSIZED_GROUP", "合併群組過大", "合併超過十筆名單；需檢視合併邊。", "ProfileMerge", Identity + "#reason-code-registry"),

        // Retrieval routes.
        ("RETRIEVAL_ROUTE", "EXACT_ALIAS", "別名完全相符", "正規化後的別名與查詢完全相同；永不截斷。", "ProfileIndex.Search", Identity + "#retrieval-modes"),
        ("RETRIEVAL_ROUTE", "BM25", "詞彙檢索", "以 BM25 依詞彙排序。", "ProfileIndex.Search", Identity + "#retrieval-modes"),
        ("RETRIEVAL_ROUTE", "NGRAM", "字元 n-gram", "以字元 n-gram Dice 排序。", "ProfileIndex.Search", Identity + "#retrieval-modes"),
        ("RETRIEVAL_ROUTE", "READING", "讀音", "以共同的拼音讀音排序；只是檢索路徑，不是相符。", "ProfileIndex.Search", Identity + "#retrieval-modes"),
        ("RETRIEVAL_ROUTE", "PROFILE_ID", "名單編號", "以名單編號直接取得。", "ListService", Identity + "#retrieval-modes"),
        ("RETRIEVAL_ROUTE", "PREVIOUS_CASE_INPUT", "前次案件輸入", "取自該案件前次分析的名單。", "ListService", Identity + "#retrieval-modes"),
        ("RETRIEVAL_ROUTE", "CASE_TABLE", "案件疑似名單表", "取自案件資訊的疑似符合名單表。", "ListService", Identity + "#retrieval-modes"),

        // API problems (RFC 9457 type /api/codes/{code}) and the input problems listed in their errors[].
        ("PROBLEM", "INVALID_REQUEST", "請求無效", "參數或內容不符合端點要求；依 fix 修正後重送。", "Api", Api + "#errors"),
        ("PROBLEM", "UNSUPPORTED_FILE", "不支援的檔案", "只接受 .txt、.html、.htm，每檔 5 MB 以內。", "Uploads", Api + "#errors"),
        ("PROBLEM", "INPUT_INCOMPLETE", "輸入檔不完整", "缺少必要檔案：恰好一份案件資訊，以及受檢對象自己的背景資料檔。缺名單檔不是錯誤（該名單為 NOT_EVALUATED）。", "TxtCaseReader.RequiredFileProblem", Api + "#errors"),
        ("PROBLEM", "INPUT_UNREADABLE", "輸入檔無法讀取", "檔案不是預期的匯出版面；errors[] 指出檔案、表格與問題。", "TxtCaseReader", Api + "#errors"),
        ("PROBLEM", "NOT_FOUND", "找不到", "案件、revision、批次、名單或路徑不存在。", "Api", Api + "#errors"),
        ("PROBLEM", "HTTP_ERROR", "HTTP 錯誤", "其他 4xx，例如方法不允許；依 status 處理。", "Api", Api + "#errors"),
        ("PROBLEM", "INTERNAL_ERROR", "系統錯誤", "未預期的錯誤；不需修改請求，稍後重試或通報。", "Api", Api + "#errors"),
        ("JOB_ERROR", "INVALID_INPUT", "輸入不可用", "批次項目的檔案不完整或不是匯出版面；修正檔案後另送新批次，重跑會同樣失敗。", "BatchService", Api + "#durable-batch-execution"),
        ("JOB_ERROR", "INVALID_PAYLOAD", "工作內容無法讀取", "未知的工作類型、無法解析的 JSON 或缺欄位；立即進入 DEAD_LETTER，重跑會同樣失敗。", "BatchService", Api + "#durable-batch-execution"),
        ("JOB_ERROR", "TIMEOUT", "逾時", "工作超過 Batch:Timeout，不寫入 revision；依重試預算重試。", "BatchService", Api + "#durable-batch-execution"),
        ("JOB_ERROR", "LEASE_EXPIRED", "租約逾期", "工作者中途停止，租約到期後工作回到佇列；預算用盡則進入 DEAD_LETTER。", "JobStore.ReclaimExpiredAsync", Api + "#durable-batch-execution"),
        ("INPUT_PROBLEM", "MISSING", "缺檔", "必要檔案不存在。", "TxtCaseReader.RequiredFileProblem", Api + "#errors"),
        ("INPUT_PROBLEM", "DUPLICATE", "重複", "同一份必要檔案出現兩次，或兩個檔案讀成同一名稱。", "TxtCaseReader", Api + "#errors"),
        ("INPUT_PROBLEM", "MISSING_SECTION", "缺少區段", "案件資訊沒有「疑似符合的名單資料」區段。", "TxtCaseReader.ReadCaseTable", Api + "#errors"),
        ("INPUT_PROBLEM", "MISSING_COLUMN", "缺少欄位", "疑似符合的名單資料缺少必要欄位。", "TxtCaseReader.ReadCaseTable", Api + "#errors"),
    }.ToDictionary(e => e.Code, e => new CodeEntry(e.Code, e.Kind, e.Name, e.Description, e.Rule, e.Doc), StringComparer.Ordinal);
}
