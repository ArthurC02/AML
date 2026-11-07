# Web Text

`Infra/Web` fetches web pages and PDFs and keeps their readable text. It is used to gather training and evaluation text for the news models; no case analysis reads it yet.

## Fetching

`PoliteFetcher` fetches as a well-behaved crawler:

- **robots.txt** (RFC 9309), read once per site and at most 512 KiB of it:
  - The groups naming the crawler's product token (`AmlPoC-research` in its agent string) are followed, else the `*` groups. Lines such as `Sitemap` neither belong to a group nor end one.
  - The longest matching `Allow` or `Disallow` wins, and `Allow` wins a tie. `*` matches any characters and a final `$` ends the path.
  - Rules are compared with the path as sent, percent-encoded. A rule written in Chinese (`Disallow: /政策/`) matches.
  - A missing `robots.txt` (4xx other than 429) allows everything.
  - A failing one (5xx, 429, unreachable, timed out) means the site is not crawled for now. The failure is not remembered, so the next request asks again.
- **Delay**: it waits between requests to one site, the configured delay or the site's `Crawl-delay` (at most a minute) if longer. The `Crawl-delay` holds from the `robots.txt` request on.
- **Redirects**: it follows them itself, at most 5. Each hop is a new request, checked against its own site's `robots.txt` and delay.
- It sends one request at a time, with a generic agent name and no contact details.
- It takes only HTML and PDF replies (media type case-insensitive; `application/x-pdf` counts as PDF). It stops reading past 10 MB.

## Main text of a web page

`MainText` keeps a page's main text and drops its menus, headers, footers, share buttons and link lists. The method follows Readability (Mozilla) and boilerpipe (Kohlschütter et al. 2010):

1. Boilerplate is removed, but only while the element holds less than half the page's text. So a WebForms `<form>` or a wrapper named `no-sidebar` around the whole page stays. Two kinds of element are removed:
   - Elements that never hold content: script, style, nav, header, footer, aside, iframe, form controls and the like.
   - Elements with a class or id token naming boilerplate: nav, menu, footer, header, sidebar, breadcrumb, share, social, related, comment, pager, copyright, print … Whole tokens count, split at spaces, `-` and `_`, so `shareholder-info` is not `share`.
2. Each paragraph-like element with at least 20 characters of its own text gives its parent a score: 1, plus its commas (，、。；,), plus its length in hundreds (at most 3). Its grandparent gets half that score. Text written in a `div` with `<br>` counts, not only `<p>`.
3. The main text is the container with the best score times one minus its share of link text. All links count, so a long link list scores as links.
4. Inside it, any block whose text is more than half link text is dropped (上一則／下一則, 相關新聞).
5. The text has one line per block element and per `<br>`, with spaces collapsed:
   - A line break in the source is a space, except in `<pre>`.
   - Inline elements (a, span, del, ruby …) stay in their line.
   - Table cells of a row are one line, separated by spaces.
6. The title is the first `h1` of the main text. Failing that, it is the first `h1` outside a header or nav, then the page title.

The page is decoded by its meta charset (UTF-8, Big5); a charset given only in the HTTP header is not used. Parsing has the time budget of the HTML reader ([extraction](extraction-and-normalization.md)), and a page nesting deeper than 256 levels is refused. Every element's text, link and comma counts are summed once, bottom up, so the rest of the work is linear in the page.

## PDF text

`PdfText` reads a PDF's text in content order (PdfPig). The prosecutors' offices publish their press releases only as PDF.

- A page number standing as a page's first or last line is dropped: `- 1 -`, `1`, `第 2 頁，共 3 頁`, `3/3`, `Page 1 of 2`. A number inside the text, such as an amount on its own line, is kept.
- In a document of two or more pages, any line found on at least half the pages (and at least two) is dropped as letterhead or footer. Digits are ignored when lines are compared.
- A PDF of more than 200 pages or 2,000,000 characters is refused. So is an unreadable or encrypted one. Every refusal is a `FormatException`.
- PdfPig has no time or decompression budget of its own; the fetcher's 10 MB cap is the only bound.
- A PDF whose fonts carry no character map yields no usable text.

## Site templates

A site prints the same lines on every document: the office's letterhead and English heading, the spokesperson and telephone block, fixed notices. Most press releases are one page, so a PDF's own pages do not show them. `RepeatedLines.WithoutSiteTemplate` takes a site's documents together and drops any line found in at least 3 of them and in a fifth of them. Digits are ignored when lines are compared. Two documents sharing a line are not a template.

## Sources whose text may be used

Only text that may be used to train and evaluate models is gathered ([ADR-0029](../adr/0029-clean-licensed-models-and-text.md)):

| Source | Why it may be used |
|---|---|
| Government press releases (法務部, 地方檢察署, 金管會) | 著作權法 第 9 條: a 公文, including a 新聞稿 drafted by a public servant in office, is not protected by copyright |
| Court judgments (司法院資料開放平臺) | 著作權法 第 9 條; the platform's API requires an account and asks that a judgment removed from it be deleted |
| News the bank has licensed | only when the contract allows text and data mining and model training |

Ordinary press articles are not gathered. Only a report of pure fact (著作權法 第 9 條 第 1 項 第 4 款) is outside copyright, and most articles are more than that.

Gathered text is kept outside the repository and never committed. Press releases and judgments name people, so the text is personal data under the 個人資料保護法 ([security and privacy](security-and-privacy.md)).
