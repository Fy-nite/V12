# V12 Commercial Exception

**Version 1.0** · effective 2026-11-24

V12 is free software under the GNU Affero General Public License, version 3.0
("AGPL"). This document adds a **commercial exception** on top of it, so that
small studios can ship V12-based products under the AGPL without paying a fee,
and larger ones can buy the right to keep their source closed.

Nothing in this document takes away a right you already have under the AGPL. If
you would rather comply with the AGPL in full, ignore this document entirely and
do that.

> This is a license document, not legal advice. If the outcome matters to your
> business, have a lawyer read it.

---

## The short version

| Tier | Net revenue per calendar year | Fee | Can you keep your source closed? |
| --- | --- | --- | --- |
| **Free** | under $1,000 | $0 | No. You use V12 under the AGPL. |
| **Indie** | $1,000 to $10,000 | $200 / year | Yes |
| **Studio** | $10,000 to $100,000 | $500 / year | Yes |
| **Enterprise** | over $100,000 | 1% of net revenue, minimum $2,000 / year | Yes |

- One license covers **one studio and all of its titles**, not one license per
  game.
- The tier is set from **last year's** net revenue, and moves **up immediately**
  if you cross a band during the year. Crossing is billed pro-rata, so you never
  get a retroactive bill for months you spent in a lower band.
- If you make less than $1,000 a year, you owe nothing. You do not need to
  contact anyone.
- The paid tiers exist for one concrete reason: they let you distribute your
  game **without publishing its source code**. Under the plain AGPL, shipping a
  V12-based game means honoring the AGPL's source obligations. Section 8 sets
  out exactly what the closed-source grant does and does not cover.
- Licensing questions: [finite.ovh](https://finite.ovh), or open an issue at
  [github.com/Fy-nite/V12/issues](https://github.com/Fy-nite/V12/issues).

---

## 1. Definitions

**1.1 "AGPL"** means the GNU Affero General Public License, version 3.0, the
text of which ships in `LICENCE` at the root of this repository.

**1.2 "V12"** means the software covered by Section 3, in source or compiled
form, including any modification of it.

**1.3 "Covered Work"** means a product that incorporates V12: a game,
application, tool, simulation, service, or piece of interactive content that you
build with V12 and distribute to other people. A Covered Work is not V12 itself.

**1.4 "You"** and **"Studio"** mean the individual or legal entity exercising
rights under this document, together with every entity that controls, is
controlled by, or is under common control with it. Revenue is measured across
that whole group. You may not split one business across several entities to stay
in a lower tier.

**1.5 "Net Revenue"** means money you actually receive from a Covered Work,
calculated as set out in Section 6.

**1.6 "Calendar Year"** means 1 January to 31 December inclusive.

**1.7 "Commercial Distribution"** means distributing, publishing, selling, or
operating a Covered Work in a way that produces Net Revenue, or in the course of
a business, including distribution through a storefront, publisher, platform, or
as a hosted service.

**1.8 "Closed-Source Grant"** means the permission described in Section 8.

**1.9 "Finite"** and **"Copyright Holder"** mean Finite, the copyright holder of
V12, and its successors.

**1.10 "Exception Version"** means the version number at the top of this
document. Section 15 explains which version applies to which year.

---

## 2. Relationship to the AGPL

**2.1 This document is an additional permission.** Under section 7 of the AGPL,
the Copyright Holder may grant additional permissions on top of the license. The
closed-source grant in Section 8 is such a permission, granted to the studios
that hold a paid license. It does not replace the AGPL; it adds to it.

**2.2 Nothing here restricts the AGPL.** This document is not a further
restriction on your AGPL rights. You may always take V12 under the plain AGPL,
sell your Covered Work, and comply with the AGPL's obligations instead of paying
Finite anything. If you do that, the revenue thresholds in this document do not
apply to you, because you are not relying on the exception.

**2.3 Where the two documents meet.** In the event of a conflict, the AGPL text
controls, except where this document grants an additional permission that
section 7 of the AGPL allows.

**2.4 What this document does not do.** It does not relicense V12, does not
grant any rights in V12's trademarks or logos, does not grant rights in
third-party components bundled with V12, and does not promise support.

---

## 3. Scope: which software this covers

**3.1 Covered software.** This exception covers the software in the V12 project
that is distributed under the AGPL-3.0, in every repository that carries this
notice. That includes the engine and runtime (`V12-Engine`, `V12.Basic`), the
renderer backends (`V12.Monogame`, `Nova`, `NovaV2`), the runtime modules
(`V12.Rendering`, `V12.Physics`, `V12.Gravity`, `V12.Audio`, `V12.Skeletal`,
`V12.Editor`, `V12.Paper`, `V12.Pak`), the tooling (`V12-Sdk` and `vcsdk`), and
anything else in this project that ships under the AGPL.

**3.2 Software that is already permissive.** `Contract`, `Contract.*`, and the
`ObjektRT` libraries are MIT licensed. You do not need this exception to use
them, and nothing in this document adds conditions to them.

| Software | License | Needs this exception? |
| --- | --- | --- |
| V12 engine, modules, SDK, renderers | AGPL-3.0 | Yes, to ship closed source |
| `Contract`, `Contract.*`, `ObjektRT.*` | MIT | No |

**3.3 Mixed V12 and Contract code.** Where a Covered Work uses both V12 and
Contract, the closed-source grant covers the V12 part. The MIT part is already
free of source obligations.

**3.4 Submodules carry their own copy.** Several of these projects live in their
own repositories. The exception applies to a copy of V12 only if that copy
carries this notice. If a repository does not include it, ask, and treat that
copy as plain AGPL until it does.

**3.5 Third-party components.** Other licenses inside V12 (for example MIT, BSD,
or Apache-licensed dependencies, third-party renderer backends, and bundled
fonts) continue to apply on their own terms. The closed-source grant does not
override them.

**3.6 Sample content.** Art, audio, and level content shipped in `V12-Samples`
and the renderer samples is provided for you to learn from and build with. It is
not licensed for standalone redistribution as an asset pack.

---

## 4. The Free Tier

**4.1 Who qualifies.** If your Studio's Net Revenue from all Covered Works is
less than $1,000 USD in a Calendar Year, you may use V12 in Commercial
Distribution with no fee and no contact with Finite.

**4.2 What you get.** The AGPL, in full, and nothing more. You do not get the
closed-source grant. If you distribute a Covered Work, the AGPL's source
obligations apply to it.

**4.3 Nothing to report.** Free Tier use requires no declaration, no audit, and
no paperwork.

**4.4 You may buy up.** You can take a paid tier at any time, including while
below $1,000, if you want the closed-source grant. Many small studios do exactly
that: the fee is small, and not publishing your game's source is worth it.

**4.5 Crossing the line.** Once your Studio's Net Revenue reaches $1,000, you
leave the Free Tier. Section 7 says what happens, and when.

---

## 5. Paid tiers

| Tier | Net Revenue per Calendar Year | Annual fee | Includes |
| --- | --- | --- | --- |
| **Indie** | $1,000 to $10,000 | $200 | Closed-source grant, written license certificate |
| **Studio** | $10,000 to $100,000 | $500 | Closed-source grant, priority support channel, optional listing as a licensee |
| **Enterprise** | over $100,000 | 1% of Net Revenue, minimum $2,000 | Closed-source grant, priority support, custom terms and SLA on request |

**5.1 What the fee buys.** A license to distribute your Covered Work with the
source disclosure obligations of the AGPL waived as described in Section 8. That
is the whole product. The exception does not include a support commitment,
warranty, indemnity, or a promise of future features, except where a separate
written agreement says so.

**5.2 Annual, not perpetual.** Paid licenses cover one Calendar Year each. They
do not vest, and they do not carry over. You renew for each year in which you
distribute.

**5.3 Fees are exclusive of tax.** Add any VAT, sales tax, or withholding that
applies. If withholding is required, tell Finite and provide the tax receipt.

**5.4 Enterprise minimum.** The $2,000 minimum applies to a full year. A
crossing year is pro-rated under Section 7.

---

## 6. How Net Revenue is calculated

The threshold is deliberately forgiving. Count what you actually keep.

**6.1 Counted as revenue.** Money received from a Covered Work, from any source:

- sales, pre-orders, and paid downloads, including early access;
- in-app purchases, DLC, season passes, and virtual currency;
- subscriptions, battle passes, and recurring memberships;
- advertising, sponsorship, and brand deals;
- donations, tips, and crowdfunding, including Kickstarter and Patreon;
- grants, prizes, and competition awards paid to the Studio;
- publisher advances and minimum guarantees, when received;
- revenue from licensing or porting a Covered Work to another platform or
  storefront;
- merchandise sold as part of the Covered Work's brand.

**6.2 Not counted.** Subtract these before testing the threshold:

- refunds, chargebacks, and fraud losses;
- storefront and platform commission, including the standard cut taken by Steam,
  itch.io, the App Store, Google Play, and console holders;
- payment processor fees;
- VAT, sales tax, and other transaction taxes you collect and remit;
- amounts invoiced but never received;
- investment capital, loans, and convertible notes;
- tax credits, rebates, and government relief payments;
- revenue from products that do not incorporate V12;
- money you receive as an employee or contractor salary.

**6.3 Currency.** Convert to USD at the rate you actually received, using a
published reference rate for that date, and keep the rate you used. Pick one
source (for example the European Central Bank reference rate) and stay
consistent.

**6.4 What is "attributable".** If a Studio sells both V12 and non-V12 products,
only revenue from Covered Works counts. If one purchase bundles a Covered Work
with something else, count the Covered Work's share on a reasonable, documented
basis.

**6.5 Timing.** Revenue counts in the Calendar Year you receive it, not the year
you earned it or the year the game shipped.

**6.6 Examples.**

| Situation | Net Revenue | Tier |
| --- | --- | --- |
| Free jam game, $400 in tips | $400 | Free |
| $2 game, 400 copies, ~30% store cut | about $560 | Free |
| Ad-supported game paying $300 after the network's cut | $300 | Free |
| Kickstarter that raises $1,200, less fees and rewards | about $900 | Free |
| Same game, next year, earning $1,200 | $1,200 | Indie, $200 |
| Studio with three titles totalling $18,000 | $18,000 | Studio, $500 |
| Title earning $150,000 | $150,000 | Enterprise, $2,000 minimum (1% would be $1,500) |
| Title earning $400,000 | $400,000 | Enterprise, 1% = $4,000 |

---

## 7. Thresholds, crossings, and pro-rata billing

**7.1 Which tier applies.** Your tier for a Calendar Year is set from your
Studio's Net Revenue in the **previous** Calendar Year. A new Studio with no
previous year starts in the Free Tier and moves up under 7.2 as revenue arrives.

**7.2 Moving up is immediate.** If, during a year, your cumulative Net Revenue
crosses into a higher band before 31 December, the higher tier applies for the
rest of that year.

**7.3 Billing on a crossing.** When you cross a band line:

1. the obligation starts on the date you crossed, not 1 January;
2. you have **90 days** from that date to obtain the license, and you may keep
   distributing during those 90 days;
3. the fee for the crossing year is **pro-rated by whole days** from the
   crossing date to 31 December at 1/365 of the annual fee;
4. anything you already paid for that year is credited against the new fee;
5. the result is never a refund, but it is often zero: a studio that crosses
   late in the year has usually already paid enough.

**7.4 Moving down is not immediate.** A weak year never changes your tier
mid-year. Your tier drops on the following 1 January, based on that year's
figures, and you may return to the Free Tier only after **two consecutive**
Calendar Years below $1,000.

**7.5 No retroactive increases.** Finite will not bill you for a tier you were
not in at the time.

**7.6 One license, all titles.** A single license covers every Covered Work the
Studio distributes in that year. DLC, ports, remakes, demos, and prototypes are
not separate licenses. A sequel that ships two years later is not a new license
either, as long as the Studio's license is current.

---

## 8. The closed-source grant

This section applies to the Indie, Studio, and Enterprise tiers. It does not
apply to the Free Tier.

**8.1 The grant.** While your license is current and you are complying with this
document, Finite grants you a worldwide, non-exclusive, non-transferable,
non-sublicensable, revocable (under Section 12) permission to:

1. use and modify V12 in a Covered Work;
2. distribute that Covered Work in object-code form, with or without your own
   source, without publishing your source;
3. operate that Covered Work as a network service, without the offer of source
   that section 13 of the AGPL would otherwise require;
4. keep your modifications to V12 private.

**8.2 What it waives.** The grant waives the source disclosure obligations of
sections 5 and 6 of the AGPL (conveying source and non-source forms) and section
13 (network use), **with respect to your Covered Work and your modifications to
V12**, and only for as long as the license is current.

**8.3 What it does not waive.** Everything else in the AGPL still applies,
including:

- keeping copyright, license, and attribution notices intact in the files you
  distribute;
- the disclaimer of warranty and limitation of liability;
- the patent grant in section 11 of the AGPL, which terminates if you bring a
  patent suit alleging that V12 infringes;
- the requirement not to add further restrictions on recipients' rights in V12
  itself.

**8.4 You may not resell V12.** The grant covers embedding V12 in your product.
It does not let you distribute V12 as a standalone engine, SDK, framework,
template, boilerplate, or asset to other developers, whether modified or not,
and whether free or paid. If that is your business, talk to Finite about a
distribution agreement.

**8.5 Contractors and publishers.** You may let contractors work on your Covered
Work and let a publisher distribute it, provided they act on your behalf, under
written terms at least as protective as this section, and do not gain any
independent right to V12.

**8.6 Your own code stays yours.** Finite claims no ownership of, or license to,
the code, art, audio, and content you create for a Covered Work. The grant runs
one way.

---

## 9. Your obligations

To rely on this exception, a Studio in Commercial Distribution must:

1. **Keep the notices.** Do not remove or alter copyright, license, or
   attribution notices from V12 files you distribute.
2. **Credit V12.** Include `Powered by V12` in your in-game credits, or, if your
   product has no credits screen, in the store listing or product page, with a
   link to <https://github.com/Fy-nite/V12>. This applies to every tier,
   including Free. Purely personal, non-commercial use needs no credit.
3. **Do not imply endorsement.** Do not state or imply that Finite made,
   sponsors, or endorses your Covered Work, and do not use the V12 or Finite
   names or logos as your own branding.
4. **Do not misrepresent the license.** Do not tell your users or your
   distributors that V12 is proprietary, or that they have rights in V12 that
   they do not have.
5. **Pay on time.** Fees are due within 30 days of invoice.
6. **Keep records.** Keep revenue records sufficient to show your tier for three
   Calendar Years (Section 10).

---

## 10. Reporting, records, and audit

**10.1 Annual declaration.** Indie, Studio, and Enterprise licensees must
declare their previous Calendar Year's Net Revenue by **15 February** each year,
or with their renewal, whichever is later. A short statement of the figure and
the band is enough. No accounting package, no line-by-line export.

**10.2 Records.** Keep records supporting the declared figure for three Calendar
Years after the declaration.

**10.3 Audit.** Finite may review those records at most **once per Calendar
Year**, on at least 30 days' written notice, limited to establishing the correct
tier. Reviews happen remotely unless there is a real reason to visit.

**10.4 Cost.** Finite pays for the review, unless it finds an understatement of
more than 5% of the correct figure, in which case the licensee pays the
reasonable cost of the review.

**10.5 Confidentiality.** Figures disclosed under this section are confidential.
Finite will not publish them or share them except with professional advisers
under confidentiality, or where the law requires.

**10.6 Misrepresentation.** If a licensee materially understates revenue, the
license terminates under Section 12 and the licensee owes the difference between
the fees paid and the fees owed for each affected year. Good-faith mistakes,
corrected promptly on discovery, are handled by paying the difference, not by
termination.

---

## 11. Term, renewal, and what happens if revenue falls

**11.1 Term.** A paid license covers the Calendar Year in which it is issued or
renewed, through 31 December. A license bought mid-year is pro-rated under
Section 7.3.

**11.2 Renewal.** Finite will send a renewal reminder at least 30 days before
expiry. If you do not renew and you continue Commercial Distribution, you are
distributing without a license. Fix it or fall back to the AGPL.

**11.3 Falling below a band.** If your Net Revenue drops into a lower band, your
tier changes on the following 1 January, subject to the two-year rule for the
Free Tier in Section 7.4.

**11.4 Returning to Free.** After two consecutive Calendar Years below $1,000,
you may return to the Free Tier. Until then, keep your license current if you
are distributing. This rule exists so that a single weak year does not become a
free pass on a business that was previously commercial.

**11.5 Grace on expiry.** If you are mid-renewal and it is not your fault, tell
Finite before the expiry date and you will get up to 30 days of grace.

---

## 12. Termination and cure

**12.1 Cure period.** If you breach this document, Finite will give you written
notice and **30 days** to fix it. Most breaches (a missed renewal, a late
declaration) are fixed by paying or filing.

**12.2 Termination.** If the breach is not fixed within the cure period, the
license terminates at the end of it.

**12.3 Effect of termination.** On termination you must stop Commercial
Distribution of Covered Works under the closed-source grant. You may either:

1. start complying with the AGPL in full and keep distributing, or
2. stop distributing.

**12.4 Survival.** Payment obligations for years already licensed, the audit
right for those years, the confidentiality in Section 10.5, and Sections 8.6, 13,
and 14 survive termination.

**12.5 No penalty beyond the fee.** Termination is not a penalty clause. Finite
does not claim damages for the act of termination itself, only amounts actually
owed.

---

## 13. Out of scope

**13.1 Contract is MIT.** The Contract language, its compiler, and the ObjektRT
libraries are MIT licensed. You do not need this exception to use them, and this
document imposes nothing on them.

**13.2 Third-party code.** Rights in third-party dependencies come from their own
licenses, not from this document.

**13.3 Trademarks.** No trademark, trade name, or logo license is granted. Ask
before using the V12 or Finite marks in marketing, and do not use them in a way
that suggests endorsement.

**13.4 Support and warranty.** Paid tiers include access to a support channel.
They do not include a warranty, an indemnity, a service level, or a commitment to
fix anything, unless a separate signed agreement says so. V12 is provided as-is,
as the AGPL already states.

**13.5 Legal advice.** This document is not legal advice.

---

## 14. Contributions

**14.1 Why this section exists.** Finite can only sell closed-source licenses
for code it is allowed to relicense. If you contribute to V12, this section
applies to your contribution.

**14.2 The grant.** By submitting a contribution to a V12 project, you grant
Finite a perpetual, worldwide, royalty-free, irrevocable license to reproduce,
modify, distribute, sublicense, and **relicense** your contribution under any
terms, including proprietary and commercial terms, and you confirm you have the
right to do so.

**14.3 What you keep.** You keep copyright in your contribution and may use it
however you like elsewhere.

**14.4 If you cannot grant this.** Do not submit the contribution. Open an issue
describing the change instead.

---

## 15. Changes to this document

**15.1 Versioned.** This document is versioned. Each version is published in the
repository and takes effect on its stated date.

**15.2 Not retroactive.** A new version does not change the terms that applied to
a year that has already ended.

**15.3 Which version governs a year.** The Exception Version in force on
1 January of a Calendar Year governs that year, even if a later version is
published during it. If a later version is more favorable to you, you may elect
it for the current year by telling Finite.

**15.4 Fees.** Finite may change fees in a new version. A fee change takes effect
no earlier than the next 1 January following publication, and never mid-term.

---

## 16. Contact

- Website: <https://finite.ovh>
- Issues and licensing requests: <https://github.com/Fy-nite/V12/issues>
- Source: <https://github.com/Fy-nite/V12>

When you write about a license, include:

- your studio or your name;
- the titles you plan to ship, and the platforms;
- your best estimate of the coming year's revenue band;
- the tier you think applies.

That is usually enough for a same-week answer.

---

## FAQ

**My game is free and I take donations. Do I need a license?**
No, if donations stay under $1,000 net for the year. You still owe a credit
line, and the AGPL still applies to your source.

**I'm under $1,000 but I don't want to publish my source.**
Take the Indie license at $200. The threshold sets when payment becomes
mandatory, not the only reason to buy.

**I'm a solo dev and my game made $1,100. Do I really owe $200?**
Less than that. The fee is pro-rated from the day you crossed, so it is a
fraction of $200 depending on when in the year it happened. The full $200 applies
from the following 1 January. If even that is not worth it, stay on the AGPL and
publish your source.

**Do I count revenue from a game that doesn't use V12?**
No. Only Covered Works count.

**Do I count a publisher's advance?**
Yes, when you receive it. Recoupable advances count as received; a loan does
not.

**Does a Kickstarter count in the year it pays out, even if the game ships later?**
Yes. Revenue counts when you receive it.

**What if my revenue jumps from $9,000 to $40,000 mid-year?**
Indie to Studio. You have 90 days to buy the $500 annual license, billed
pro-rata from the crossing date, less any Indie fee already paid.

**What about the jump to Enterprise at $100,000?**
The same mechanics: 1% of the year's Net Revenue from the crossing date, with a
pro-rated $2,000 minimum, credited against what you already paid.

**Is the $100,000 measured in one year or lifetime?**
One Calendar Year, each year, separately.

**We are a studio with three games. Do we pay three times?**
No. One license per studio, covering all titles, and the tier is set on combined
Net Revenue.

**Can we split into two companies to stay in a lower tier?**
No. Section 1.4 counts controlled entities together.

**We use V12 for a hosted service, not a game. Does that count?**
Yes. Section 8.1(3) covers network use, and the AGPL's section 13 source offer is
waived while your license is current.

**Can we sell a V12-based engine, template, or SDK of our own?**
Not under this exception. That is redistribution of V12 as a product, not
embedding it in a Covered Work. Talk to Finite.

**Can we hire a contractor to work on our V12 fork?**
Yes, under Section 8.5, as long as they work for you and gain no independent
rights.

**What if our store takes 30% and we never see it?**
You never count it. Section 6.2 excludes storefront commission.

**What currency do we report in?**
USD, converted at a published rate on the date of receipt. Keep the rates.

**Do we have to put "Powered by V12" on a free game jam entry?**
Only for Commercial Distribution. A free jam entry with no revenue and no
business purpose needs no credit.

**We already ship under the AGPL and publish our source. Why would we pay?**
You wouldn't. The paid tiers exist for studios that want to keep their source
closed. Everything else in the AGPL is available to you for free.

**What if Finite disappears or stops maintaining V12?**
Your paid license is for the year you bought. The AGPL always remains available
as a fallback, and it cannot be revoked.

**Who do I ask if my case isn't covered?**
Open an issue at <https://github.com/Fy-nite/V12/issues> with the details listed
in Section 16. A real answer beats a guess.
