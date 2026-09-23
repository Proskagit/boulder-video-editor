# Architecture

This document describes the verified architecture of the AI Video Editor.

## Current known stack

- .NET 8
- C#
- Avalonia
- CommunityToolkit.Mvvm
- Serilog
- FFmpeg / FFprobe

## Solution

The solution currently contains 11 projects.

The exact project responsibilities should be filled in after Claude inspects the solution. Do not invent project boundaries.

## Core domain

Known domain concepts include:
- Project
- Track
- Clip
- Timeline
- ExportSettings
- MediaTime

### MediaTime

MediaTime uses 100-nanosecond ticks.

This is a deliberate precision decision and should be preserved unless an explicit architectural decision changes it.

## Media pipeline

FFmpeg / FFprobe are used for media analysis and processing.

The existing implementation should be inspected before introducing changes.

## MVVM

CommunityToolkit.Mvvm is part of the existing stack.

Prefer the established project conventions for:
- ViewModels
- Observable properties
- Commands
- Binding
- Services

## Logging

Serilog is part of the existing stack.

Prefer the existing logging abstraction and configuration.

## Architecture change policy

Major architectural changes require explicit user approval.

Routine refactoring needed to implement a feature does not.

## Verification note

This file is intentionally conservative. Claude must inspect the actual solution before filling in missing architectural details.
