# Sharpie

A POSIX-style shell written in C#, with its own Avalonia terminal window.

The window (`src/Sharpie`) is the app, a terminal emulator. It starts the shell engine (`src/Sharpie.Shell`) in a
pseudo-terminal (ConPTY on Windows) and draws its output on a character grid, so the shell and every program it runs
see a real terminal: `[Y/n]` and password prompts, `vim`, `htop` and mouse reporting all work. The engine handles
builtins (`cd`, `echo`, `type`, `history`, `jobs`, `fg`, `bg`, `declare`, `export`, `unset`, `.`, `complete`, ...),
running programs, pipelines, redirects and POSIX job control, and its line editor draws the prompt header with folder
and git branch, syntax coloring, Tab completion and Up/Down history. The window adds a built-in `man` pager.

## Run

```sh
dotnet run --project src/Sharpie
```

## Startup file

An interactive shell runs `~/.sharpierc` (`%USERPROFILE%\.sharpierc` on Windows) before its first prompt, the way bash
runs `~/.bashrc`. It holds ordinary command lines, run in order in the shell itself:

```sh
# ~/.sharpierc
export PAGER=less
export PATH="$PATH:$HOME/.dotnet/tools"
alias ls='ls -p --color=auto'
HISTFILE=$HOME/.sharpie_history
. $HOME/.sharpie_aliases
```

A line ending in `\` continues on the next one. `Sharpie.Shell --norc` skips the file, and `. file` or `source file`
runs another one at any time.

## Test

```sh
dotnet test Sharpie.slnx
```

## Layout

Each project is organized by feature: `Features/<Feature>/` holds everything for one feature, and `Common/` holds what
several features share. Tests mirror the same folders.

## License

Copyright (C) 2026 Nathan Price

Sharpie is free software: you can redistribute it and/or modify it under the terms of the
GNU General Public License as published by the Free Software Foundation, either version 3
of the License, or (at your option) any later version. See [LICENSE](LICENSE).
