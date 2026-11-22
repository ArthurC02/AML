#!/bin/bash
# usage: e2e.sh <tag> [pair]   the working end-to-end measure (the former aml-eval-r4/both.sh): builds the .NET evaluator,
# scores the dev documents and the second dev set against all labelled events at 0.3 through OnnxNewsModel and
# NewsModelReading, prints the fragment score, then the default (persons written identically are one name).
# pair: a folder name under AML_TRAIN/measurements (default aml43-pair, the retained NER20/event43) or a path; it holds
# ner/ and ev/ (model.onnx, config.json, vocab.txt, NER document-roles.onnx) and the caches cache/ and cache-dev2/.
# The evaluator lives outside the repo in AML_EVAL (Program.cs, aml-eval.csproj; it compiles the reader from a checkout
# and reads its dev.jsonl / train.jsonl documents from fixed paths inside Program.cs). Dumps e2e-dev{1,2}-<tag>.tsv
# (numbers and anonymous ids only) are written in AML_EVAL. Counts only.
tag=$1; pair=${2:-aml43-pair}
T=${AML_TRAIN:-C:/tmp/aml-train}; E=${AML_EVAL:-C:/tmp/aml-eval-r4}; D=${AML_DATA:-C:/tmp/aml-data}
PY=${AML_PY:-$T/.venv/Scripts/python.exe}; HERE=$(cd "$(dirname "$0")" && pwd)
[ -n "$tag" ] || { echo "usage: e2e.sh <tag> [pair]"; exit 64; }
if [ -d "$pair" ]; then P=$(cd "$pair" && { pwd -W 2>/dev/null || pwd; }); else P=$T/measurements/$pair; fi  # absolute: cd "$E" follows
cd "$E" || { echo "no evaluator folder $E (AML_EVAL)"; exit 1; }
dotnet build aml-eval.csproj -c Release 2>&1 | grep -E "error|Warn|Elapsed" | head -5
dotnet bin/Release/net10.0/aml-eval.dll $P dev cached e2e min=0.3 cache=$P/cache idsref=$T/measurements/ner-ascii-validation/token-ids-dev.jsonl dump=e2e-dev1-$tag.tsv 2>&1 | grep E2E
dotnet bin/Release/net10.0/aml-eval.dll $P train e2e min=0.3 cache=$P/cache-dev2 dump=e2e-dev2-$tag.tsv 2>&1 | grep E2E
"$PY" -I "$HERE/gold_merge.py" dev e2e-dev1-$tag.tsv 0.3 $D/labels/review-current/fix/ | sed -n 2,3p | sed 's/^/old dev | /'
"$PY" -I "$HERE/gold_merge.py" dev2 e2e-dev2-$tag.tsv 0.3 | sed -n 2,3p | sed 's/^/dev-2   | /'
