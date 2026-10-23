# Built-in vocabulary data notice

The bundled data file `src/Lexico.Core/Dictionary/builtin-vocabulary.json` is a combined and transformed database licensed under **Creative Commons Attribution-ShareAlike 4.0 International (CC BY-SA 4.0)**. The application source code remains under its separate project license. The data file is 3,885,950 bytes; SHA-256: `466A0C617D36643335582B5D9FDCDF43FE721A38A90FE4DE04F3DC82C5C5EC40`.

## Attribution

- **wordfreq English large frequency data**, Robyn Speer and wordfreq contributors, version 3.0.2, pinned commit [`372f6dbb3bd2ad4b675a3b18b94d44d0dd5fea8b`](https://github.com/rspeer/wordfreq/tree/372f6dbb3bd2ad4b675a3b18b94d44d0dd5fea8b). The upstream README says its data files may be redistributed under CC BY-SA 4.0. It identifies Google Books Ngrams and Syntactic Ngrams, the Leeds Internet Corpus, Wikipedia, ParaCrawl, OPUS OpenSubtitles 2018, SUBTLEX-US/UK/CH/DE/NL, and aggregate Twitter frequency data among its sources. The upstream README also requests credit to the SUBTLEX authors, Marc Brysbaert et al., and says SUBTLEX remains freely available. See the [pinned upstream README](https://github.com/rspeer/wordfreq/blob/372f6dbb3bd2ad4b675a3b18b94d44d0dd5fea8b/README.md).
- **FrequencyWords English 50k list**, hermitdave, based on OpenSubtitles 2018, pinned commit [`525f9b560de45753a5ea01069454e72e9aa541c6`](https://github.com/hermitdave/FrequencyWords/tree/525f9b560de45753a5ea01069454e72e9aa541c6). The upstream README marks its content CC BY-SA 4.0 and identifies OpenSubtitles 2018 as a source.
- **New General Service List (NGSL) 1.2** and **New Academic Word List (NAWL) 1.2**, by Charles Browne, Brent Culligan, and Joseph Phillips; both official project pages state CC BY-SA 4.0: [NGSL](https://www.newgeneralservicelist.com/new-general-service-list) and [NAWL](https://www.newgeneralservicelist.com/new-academic-word-list).
- **English Wiktionary**, Wiktionary contributors, with Mandarin translations and parts of speech extracted from the 2026-09-02 Kaikki/Wiktextract data snapshot. Wiktionary permits reuse of its entry text under CC BY-SA 4.0 and GFDL; this application uses and attributes the CC BY-SA 4.0 option. See [Kaikki raw-data information](https://kaikki.org/dictionary/rawdata.html) and [Wiktionary copyrights](https://en.wiktionary.org/wiki/Wiktionary:Copyrights).

## Changes made

The vocabulary combines frequency word forms with English Wiktionary lemmas and inflections; retains only entries with a part of speech and a Mandarin translation; removes proper-name entries, phrases, and non-lexical forms; normalizes lemmas and POS labels; deduplicates senses; and preserves duplicate lemmas as separate cards across separate decks. Source data and per-file hashes are recorded in [`manifest.json`](manifest.json). The 2.98 GB Wiktionary raw dump used for extraction is not included in the application.

The common deck uses wordfreq and FrequencyWords. The university deck uses NAWL. **IELTS 备考核心** combines NGSL and NAWL as an independent study list; it is not an official IELTS vocabulary list and is not endorsed by IELTS. BNC/COCA data is not included because the specific file-level redistribution license could not be verified.

To comply with ShareAlike, the combined vocabulary data is made available under the same CC BY-SA 4.0 license. This attribution does not imply endorsement by any data provider or exam organization.
