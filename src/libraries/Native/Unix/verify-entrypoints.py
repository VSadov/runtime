#!/usr/bin/env python
#
## Licensed to the .NET Foundation under one or more agreements.
## The .NET Foundation licenses this file to you under the MIT license.


from glob import glob
import sys
import re
import subprocess
import argparse

if __name__ == "__main__":
    from sys import stdout, stderr

    parser = argparse.ArgumentParser(description="Check if exports from Arg1 match entries in Arg2.")
    parser.add_argument('dll', help="DSO binary")
    parser.add_argument('entries', help="entrypoints.c source")

    args = parser.parse_args()
    dllEntries = subprocess.check_output(['nm', '-D', '--defined-only', '--demangle', args.dll])

    # match name in "000000000000ce50 T CryptoNative_X509StackAddMultiple"
    exportPatternStr = r'(?:\sT\s)(\S*)'
    exportPattern = re.compile(exportPatternStr)

    dllList = re.findall(exportPattern, dllEntries)

    # match name in "DllImportEntry(CryptoNative_BioRead)"
    entriesPatternStr = r'(?:DllImportEntry\()(\S*)\)'
    entriesPattern = re.compile(entriesPatternStr)

    with open(args.entries, 'r') as f:
        entriesList = re.findall(entriesPattern, f.read())

    dllSet = set(dllList)
    entriesSet = set(entriesList)

    diff = dllSet ^ entriesSet

    if len(diff) > 0:
        stderr.write("DIFFERENCES FOUND:")
        stderr.write(diff)
        exit(1)
