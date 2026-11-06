using System.Text;

namespace Aml.Core.Normalization;

/// <summary>
/// ISO 3166-1 alpha-2 code for a country value: the alpha-2 or alpha-3 code (upper case only, so the words "NO" or
/// "in" in another casing are not codes), the ISO English short name, a few common English and Chinese names, and the
/// Taiwan aliases. Anything else is null: it is not mapped unambiguously.
/// ponytail: English and Chinese common names cover what bank and list data usually write; extend <see cref="Aliases"/>
/// from reviewed cases (no transliteration or fuzzy match, a wrong country is worse than none).
/// </summary>
internal static class Countries
{
    // alpha-2|alpha-3|ISO 3166-1 English short name (ISO Online Browsing Platform).
    private const string Iso = """
        AD|AND|Andorra
        AE|ARE|United Arab Emirates
        AF|AFG|Afghanistan
        AG|ATG|Antigua and Barbuda
        AI|AIA|Anguilla
        AL|ALB|Albania
        AM|ARM|Armenia
        AO|AGO|Angola
        AQ|ATA|Antarctica
        AR|ARG|Argentina
        AS|ASM|American Samoa
        AT|AUT|Austria
        AU|AUS|Australia
        AW|ABW|Aruba
        AX|ALA|Åland Islands
        AZ|AZE|Azerbaijan
        BA|BIH|Bosnia and Herzegovina
        BB|BRB|Barbados
        BD|BGD|Bangladesh
        BE|BEL|Belgium
        BF|BFA|Burkina Faso
        BG|BGR|Bulgaria
        BH|BHR|Bahrain
        BI|BDI|Burundi
        BJ|BEN|Benin
        BL|BLM|Saint Barthélemy
        BM|BMU|Bermuda
        BN|BRN|Brunei Darussalam
        BO|BOL|Bolivia (Plurinational State of)
        BQ|BES|Bonaire, Sint Eustatius and Saba
        BR|BRA|Brazil
        BS|BHS|Bahamas
        BT|BTN|Bhutan
        BV|BVT|Bouvet Island
        BW|BWA|Botswana
        BY|BLR|Belarus
        BZ|BLZ|Belize
        CA|CAN|Canada
        CC|CCK|Cocos (Keeling) Islands
        CD|COD|Congo, Democratic Republic of the
        CF|CAF|Central African Republic
        CG|COG|Congo
        CH|CHE|Switzerland
        CI|CIV|Côte d'Ivoire
        CK|COK|Cook Islands
        CL|CHL|Chile
        CM|CMR|Cameroon
        CN|CHN|China
        CO|COL|Colombia
        CR|CRI|Costa Rica
        CU|CUB|Cuba
        CV|CPV|Cabo Verde
        CW|CUW|Curaçao
        CX|CXR|Christmas Island
        CY|CYP|Cyprus
        CZ|CZE|Czechia
        DE|DEU|Germany
        DJ|DJI|Djibouti
        DK|DNK|Denmark
        DM|DMA|Dominica
        DO|DOM|Dominican Republic
        DZ|DZA|Algeria
        EC|ECU|Ecuador
        EE|EST|Estonia
        EG|EGY|Egypt
        EH|ESH|Western Sahara
        ER|ERI|Eritrea
        ES|ESP|Spain
        ET|ETH|Ethiopia
        FI|FIN|Finland
        FJ|FJI|Fiji
        FK|FLK|Falkland Islands (Malvinas)
        FM|FSM|Micronesia (Federated States of)
        FO|FRO|Faroe Islands
        FR|FRA|France
        GA|GAB|Gabon
        GB|GBR|United Kingdom of Great Britain and Northern Ireland
        GD|GRD|Grenada
        GE|GEO|Georgia
        GF|GUF|French Guiana
        GG|GGY|Guernsey
        GH|GHA|Ghana
        GI|GIB|Gibraltar
        GL|GRL|Greenland
        GM|GMB|Gambia
        GN|GIN|Guinea
        GP|GLP|Guadeloupe
        GQ|GNQ|Equatorial Guinea
        GR|GRC|Greece
        GS|SGS|South Georgia and the South Sandwich Islands
        GT|GTM|Guatemala
        GU|GUM|Guam
        GW|GNB|Guinea-Bissau
        GY|GUY|Guyana
        HK|HKG|Hong Kong
        HM|HMD|Heard Island and McDonald Islands
        HN|HND|Honduras
        HR|HRV|Croatia
        HT|HTI|Haiti
        HU|HUN|Hungary
        ID|IDN|Indonesia
        IE|IRL|Ireland
        IL|ISR|Israel
        IM|IMN|Isle of Man
        IN|IND|India
        IO|IOT|British Indian Ocean Territory
        IQ|IRQ|Iraq
        IR|IRN|Iran (Islamic Republic of)
        IS|ISL|Iceland
        IT|ITA|Italy
        JE|JEY|Jersey
        JM|JAM|Jamaica
        JO|JOR|Jordan
        JP|JPN|Japan
        KE|KEN|Kenya
        KG|KGZ|Kyrgyzstan
        KH|KHM|Cambodia
        KI|KIR|Kiribati
        KM|COM|Comoros
        KN|KNA|Saint Kitts and Nevis
        KP|PRK|Korea (Democratic People's Republic of)
        KR|KOR|Korea, Republic of
        KW|KWT|Kuwait
        KY|CYM|Cayman Islands
        KZ|KAZ|Kazakhstan
        LA|LAO|Lao People's Democratic Republic
        LB|LBN|Lebanon
        LC|LCA|Saint Lucia
        LI|LIE|Liechtenstein
        LK|LKA|Sri Lanka
        LR|LBR|Liberia
        LS|LSO|Lesotho
        LT|LTU|Lithuania
        LU|LUX|Luxembourg
        LV|LVA|Latvia
        LY|LBY|Libya
        MA|MAR|Morocco
        MC|MCO|Monaco
        MD|MDA|Moldova, Republic of
        ME|MNE|Montenegro
        MF|MAF|Saint Martin (French part)
        MG|MDG|Madagascar
        MH|MHL|Marshall Islands
        MK|MKD|North Macedonia
        ML|MLI|Mali
        MM|MMR|Myanmar
        MN|MNG|Mongolia
        MO|MAC|Macao
        MP|MNP|Northern Mariana Islands
        MQ|MTQ|Martinique
        MR|MRT|Mauritania
        MS|MSR|Montserrat
        MT|MLT|Malta
        MU|MUS|Mauritius
        MV|MDV|Maldives
        MW|MWI|Malawi
        MX|MEX|Mexico
        MY|MYS|Malaysia
        MZ|MOZ|Mozambique
        NA|NAM|Namibia
        NC|NCL|New Caledonia
        NE|NER|Niger
        NF|NFK|Norfolk Island
        NG|NGA|Nigeria
        NI|NIC|Nicaragua
        NL|NLD|Netherlands, Kingdom of the
        NO|NOR|Norway
        NP|NPL|Nepal
        NR|NRU|Nauru
        NU|NIU|Niue
        NZ|NZL|New Zealand
        OM|OMN|Oman
        PA|PAN|Panama
        PE|PER|Peru
        PF|PYF|French Polynesia
        PG|PNG|Papua New Guinea
        PH|PHL|Philippines
        PK|PAK|Pakistan
        PL|POL|Poland
        PM|SPM|Saint Pierre and Miquelon
        PN|PCN|Pitcairn
        PR|PRI|Puerto Rico
        PS|PSE|Palestine, State of
        PT|PRT|Portugal
        PW|PLW|Palau
        PY|PRY|Paraguay
        QA|QAT|Qatar
        RE|REU|Réunion
        RO|ROU|Romania
        RS|SRB|Serbia
        RU|RUS|Russian Federation
        RW|RWA|Rwanda
        SA|SAU|Saudi Arabia
        SB|SLB|Solomon Islands
        SC|SYC|Seychelles
        SD|SDN|Sudan
        SE|SWE|Sweden
        SG|SGP|Singapore
        SH|SHN|Saint Helena, Ascension and Tristan da Cunha
        SI|SVN|Slovenia
        SJ|SJM|Svalbard and Jan Mayen
        SK|SVK|Slovakia
        SL|SLE|Sierra Leone
        SM|SMR|San Marino
        SN|SEN|Senegal
        SO|SOM|Somalia
        SR|SUR|Suriname
        SS|SSD|South Sudan
        ST|STP|Sao Tome and Principe
        SV|SLV|El Salvador
        SX|SXM|Sint Maarten (Dutch part)
        SY|SYR|Syrian Arab Republic
        SZ|SWZ|Eswatini
        TC|TCA|Turks and Caicos Islands
        TD|TCD|Chad
        TF|ATF|French Southern Territories
        TG|TGO|Togo
        TH|THA|Thailand
        TJ|TJK|Tajikistan
        TK|TKL|Tokelau
        TL|TLS|Timor-Leste
        TM|TKM|Turkmenistan
        TN|TUN|Tunisia
        TO|TON|Tonga
        TR|TUR|Türkiye
        TT|TTO|Trinidad and Tobago
        TV|TUV|Tuvalu
        TW|TWN|Taiwan, Province of China
        TZ|TZA|Tanzania, United Republic of
        UA|UKR|Ukraine
        UG|UGA|Uganda
        UM|UMI|United States Minor Outlying Islands
        US|USA|United States of America
        UY|URY|Uruguay
        UZ|UZB|Uzbekistan
        VA|VAT|Holy See
        VC|VCT|Saint Vincent and the Grenadines
        VE|VEN|Venezuela (Bolivarian Republic of)
        VG|VGB|Virgin Islands (British)
        VI|VIR|Virgin Islands (U.S.)
        VN|VNM|Viet Nam
        VU|VUT|Vanuatu
        WF|WLF|Wallis and Futuna
        WS|WSM|Samoa
        YE|YEM|Yemen
        YT|MYT|Mayotte
        ZA|ZAF|South Africa
        ZM|ZMB|Zambia
        ZW|ZWE|Zimbabwe
        """;

    private static readonly Dictionary<string, string> TaiwanAliases = new()
    {
        ["Taiwan"] = "TW",
        ["台灣"] = "TW",
        ["臺灣"] = "TW",
        ["中華民國"] = "TW",
        ["中華民國(臺灣)"] = "TW",
        ["中華民國(台灣)"] = "TW",
        ["ROC"] = "TW",
        ["R.O.C."] = "TW",
        ["Republic of China"] = "TW",
        ["Republic of China (Taiwan)"] = "TW",
        ["Chinese Taipei"] = "TW",
    };

    private static readonly Dictionary<string, string> EnglishCommonNames = new()
    {
        ["Vietnam"] = "VN",
        ["United States"] = "US",
        ["United Kingdom"] = "GB",
        ["Great Britain"] = "GB",
        ["Russia"] = "RU",
        ["South Korea"] = "KR",
        ["Republic of Korea"] = "KR",
        ["North Korea"] = "KP",
        ["Iran"] = "IR",
        ["Syria"] = "SY",
        ["Laos"] = "LA",
        ["Bolivia"] = "BO",
        ["Venezuela"] = "VE",
        ["Tanzania"] = "TZ",
        ["Moldova"] = "MD",
        ["Turkey"] = "TR",
        ["Czech Republic"] = "CZ",
        ["Macau"] = "MO",
        ["Netherlands"] = "NL",
        ["Brunei"] = "BN",
        ["Palestine"] = "PS",
        ["Micronesia"] = "FM",
        ["Ivory Coast"] = "CI",
        ["Cote d'Ivoire"] = "CI",
        ["Cape Verde"] = "CV",
        ["Swaziland"] = "SZ",
        ["Burma"] = "MM",
        ["East Timor"] = "TL",
        ["People's Republic of China"] = "CN",
        ["Hong Kong SAR"] = "HK",
        ["Macao SAR"] = "MO",
    };

    private static readonly Dictionary<string, string> ChineseCommonNames = new()
    {
        ["越南"] = "VN",
        ["中國"] = "CN",
        ["中國大陸"] = "CN",
        ["中华人民共和国"] = "CN",
        ["中華人民共和國"] = "CN",
        ["香港"] = "HK",
        ["澳門"] = "MO",
        ["日本"] = "JP",
        ["美國"] = "US",
        ["英國"] = "GB",
        ["印尼"] = "ID",
        ["印度尼西亞"] = "ID",
        ["菲律賓"] = "PH",
        ["泰國"] = "TH",
        ["馬來西亞"] = "MY",
        ["新加坡"] = "SG",
        ["緬甸"] = "MM",
        ["柬埔寨"] = "KH",
        ["南韓"] = "KR",
        ["北韓"] = "KP",
        ["大韓民國"] = "KR",
        ["加拿大"] = "CA",
        ["澳洲"] = "AU",
        ["澳大利亞"] = "AU",
        ["紐西蘭"] = "NZ",
        ["德國"] = "DE",
        ["法國"] = "FR",
        ["印度"] = "IN",
        ["俄羅斯"] = "RU",
    };

    // Names that are not the ISO short name but name one country only. 韓國 and Korea are left out on purpose (two Koreas).
    private static readonly KeyValuePair<string, string>[] Aliases = [.. TaiwanAliases, .. EnglishCommonNames, .. ChineseCommonNames];

    private static readonly HashSet<string> Codes = [];
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Alpha3 = [];

    static Countries()
    {
        foreach (var line in Iso.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Split('|');
            Codes.Add(p[0]);
            Alpha3[p[1]] = p[0];
            Names[p[2]] = p[0];
        }
        foreach (var (name, code) in Aliases) Names[Key(name)] = code;
    }

    public static string? Lookup(string raw)
    {
        var s = Key(raw);
        return Codes.Contains(s) ? s : Alpha3.TryGetValue(s, out var a) ? a : Names.GetValueOrDefault(s);
    }

    private static string Key(string raw) =>
        string.Join(' ', raw.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
