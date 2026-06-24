#!/bin/bash
# Decompile every BF in bf_per_area/ to .flow next to it.
BFDIR="C:/Program Files (x86)/Steam/steamapps/common/Persona 4 Golden/Persona 4 golden/database/extract/bf_per_area"
DECOMP="C:/Program Files (x86)/Steam/steamapps/common/Persona 4 Golden/Persona 4 golden/database/tools/BfDecompiler/bin/Release/net9.0/BfDecompiler.dll"
DOTNET="/c/Program Files/dotnet/dotnet.exe"

ok=0; fail=0
for bf in "$BFDIR"/*.bf; do
    name=$(basename "$bf" .bf)
    out="$BFDIR/${name%__*}.flow"
    if "$DOTNET" "$DECOMP" "$bf" "$out" >/dev/null 2>&1; then
        ok=$((ok+1))
    else
        fail=$((fail+1))
        echo "  FAIL: $name"
    fi
done
echo "Decompiled $ok, failed $fail"
