#!/usr/bin/env python
#
## Licensed to the .NET Foundation under one or more agreements.
## The .NET Foundation licenses this file to you under the MIT license.


from glob import glob
import sys
import re
import argparse

if __name__ == "__main__":
    from sys import stdout, stderr

    parser = argparse.ArgumentParser(description="Check if exports from Arg1 match entries in Arg2.")
    parser.add_argument('dll', metavar='file', nargs=1, help="DSO binary")
    parser.add_argument('entries', metavar='file', nargs=1, help="entrypoints.c source")

    args = parser.parse_args()

    print('DLL: ' + args.dll)
    print('ENTRIES: ' + args.entries)

    # get exports from the DSO
    # remove predefined symbols

    # get exports from entrypoints.c

    # if an entry in DSO is not matched in entrypoints.c complain

    with open(args.entries, 'r') as f:
        print(f.read())
