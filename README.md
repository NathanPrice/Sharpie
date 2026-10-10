# Sharpie

A POSIX-style shell written in C#, with its own Avalonia terminal window.

The window (`src/Sharpie`) is the app, a terminal emulator. It starts the shell engine (`src/Sharpie.Shell`) in a
pseudo-terminal (ConPTY on Windows) and draws its output on a character grid, so the shell and every program it runs
see a real terminal: `[Y/n]` and password prompts, `vim`, `htop` and mouse reporting all work. The engine handles
builtins (`cd`, `echo`, `type`, `history`, `jobs`, `fg`, `bg`, `declare`, `complete`, ...), running programs,
pipelines, redirects and POSIX job control, and its line editor draws the prompt header with folder and git branch,
syntax coloring, Tab completion and Up/Down history. The window adds a built-in `man` pager.

## Run

```sh
dotnet run --project src/Sharpie
```

## Test

```sh
dotnet test Sharpie.slnx
```

## Layout

Each project is organized by feature: `Features/<Feature>/` holds everything for one feature, and `Common/` holds what
several features share. Tests mirror the same folders.
