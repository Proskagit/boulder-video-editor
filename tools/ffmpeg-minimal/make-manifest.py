"""Writes OUT/manifest.json and OUT/MANIFEST.md for the minimal FFmpeg (DECISIONS.md D046 §13): the components with
their versions, revisions, licences and source checksums, the toolchain (image, snapshot, Debian packages), the FFmpeg
configuration, the enabled components and the binaries' sizes and SHA-256. Run by build.sh inside the toolchain image.
The manifest records facts; it is not a statement that the licence obligations are met."""
import argparse
import hashlib
import json
import os
import pathlib


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--recipe', required=True)
    p.add_argument('--sources', required=True)
    p.add_argument('--out', required=True)
    a = p.parse_args()
    recipe, sources, out = pathlib.Path(a.recipe), pathlib.Path(a.sources), pathlib.Path(a.out)

    source_sums = {}
    for line in (sources / 'SHA256SUMS').read_text().splitlines():
        digest, name = line.split(maxsplit=1)
        source_sums[name.lstrip('*').removeprefix('./')] = digest

    components = []
    for line in (recipe / 'sources.lock').read_text().splitlines():
        if not line.strip() or line.startswith('#'):
            continue
        name, kind, version, location, pin, licence, files = line.split('\t')
        archive = (f'tarballs/{os.path.basename(location)}' if kind == 'tarball' else f'git/{name}.bundle')
        components.append({
            'name': name, 'kind': kind, 'version': version, 'location': location,
            'pin': pin, 'licence': licence, 'licenceFiles': files.split(),
            'sourceArchive': archive, 'sourceArchiveSha256': source_sums.get(archive),
        })

    toolchain = []
    for line in pathlib.Path(os.environ.get('TOOLCHAIN_INFO', '/'), 'toolchain-packages.tsv').read_text().splitlines():
        pkg, ver, src, src_ver = line.split('\t')
        toolchain.append({'package': pkg, 'version': ver, 'source': src, 'sourceVersion': src_ver})

    binaries = []
    for exe in sorted((out / 'bin').glob('*.exe')):
        binaries.append({'file': f'bin/{exe.name}', 'size': exe.stat().st_size, 'sha256': sha256(exe)})

    manifest = {
        'name': 'AiVideoEditor minimal FFmpeg',
        'decision': 'DECISIONS.md D046 §13',
        'licenceOfTheBinaries': 'GPL-2.0-or-later (FFmpeg configured with --enable-gpl, without --enable-version3)',
        'image': os.environ.get('TOOLCHAIN_IMAGE', 'unknown'),
        'debianSnapshot': pathlib.Path(os.environ.get('TOOLCHAIN_INFO', '/'), 'toolchain-snapshot.txt').read_text().strip(),
        'sourceDateEpoch': os.environ.get('SOURCE_DATE_EPOCH'),
        'components': components,
        'debianSourcePackages': sorted(k for k in source_sums if k.startswith('debian/')),
        'ffmpegConfiguration': (out / 'ffmpeg-configuration.txt').read_text().strip(),
        'enabledConfig': (out / 'enabled-config.txt').read_text().split(),
        'binaries': binaries,
        'toolchainPackages': toolchain,
    }
    (out / 'manifest.json').write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + '\n', encoding='utf-8')

    md = ['# Minimal FFmpeg — build manifest', '',
          f"Decision: {manifest['decision']}. Licence of the binaries: {manifest['licenceOfTheBinaries']}.",
          'This manifest records what was built from what; it does not by itself meet the licences\' obligations.', '',
          '| Component | Version | Pin | Licence | Source archive SHA-256 |', '|---|---|---|---|---|']
    for c in components:
        md.append(f"| {c['name']} | {c['version']} | `{c['pin']}` | {c['licence']} | `{c['sourceArchiveSha256']}` |")
    md += ['', f"Toolchain image: `{manifest['image']}`; Debian snapshot `{manifest['debianSnapshot']}`.", '',
           '| Binary | Size | SHA-256 |', '|---|---|---|']
    for b in binaries:
        md.append(f"| {b['file']} | {b['size']} | `{b['sha256']}` |")
    md += ['', 'Configuration:', '', '```', manifest['ffmpegConfiguration'], '```', '']
    (out / 'MANIFEST.md').write_text('\n'.join(md), encoding='utf-8')


if __name__ == '__main__':
    main()
