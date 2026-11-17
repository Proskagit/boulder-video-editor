"""S1 (D066): re-export the person detector RF-DETR-M from Roboflow's official `rfdetr` package (Apache-2.0).

The detector of people-v1 was a third-party ONNX conversion (rtmlib's download, Hugging Face `saifkhichi96/opendetect`) whose
descent from the official checkpoint was not documented (docs/AI_MODEL.md, "People models", licences). This script exports
the official RF-DETR Medium checkpoint (576 x 576) with the package's own exporter and checks that the graph has the
interface the application uses (src/Vision/People/PersonDetector.cs): input `input` [1, 3, 576, 576] float32, outputs `dets`
[1, Q, 4] (cx, cy, w, h normalized) and `labels` [1, Q, C] logits, class 1 = person (index 0 the background).

    python -I tools/people-models/export_rfdetr.py <out folder>

Prints the checkpoint's and the ONNX file's sizes and SHA-256s. The weights are downloaded by `rfdetr` itself (its MD5 checked
by the package) into `%USERPROFILE%/.roboflow/models/rf-detr-medium.pth`.
"""
import hashlib
import importlib.metadata
import pathlib
import shutil
import sys

import onnx
import rfdetr


def sha256(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def main() -> None:
    out = pathlib.Path(sys.argv[1]).resolve()
    out.mkdir(parents=True, exist_ok=True)
    model = rfdetr.RFDETRMedium()
    resolution = model.model_config.resolution
    if resolution != 576:
        raise SystemExit(f'RF-DETR Medium resolution {resolution}, expected 576')
    work = out / 'export'
    exported = pathlib.Path(model.export(output_dir=str(work), opset_version=17, format='onnx', batch_size=1))
    if exported.is_dir():
        exported = next(exported.glob('*.onnx'))
    target = out / 'rfdetr_m_576.onnx'
    shutil.copyfile(exported, target)

    graph = onnx.load(str(target)).graph
    inputs = {i.name: [d.dim_value for d in i.type.tensor_type.shape.dim] for i in graph.input}
    outputs = {o.name: [d.dim_value for d in o.type.tensor_type.shape.dim] for o in graph.output}
    print('inputs', inputs)
    print('outputs', outputs)
    if inputs != {'input': [1, 3, 576, 576]} or set(outputs) != {'dets', 'labels'} or outputs['dets'][2] != 4:
        raise SystemExit('the exported graph does not have the interface PersonDetector uses')

    print('rfdetr', importlib.metadata.version('rfdetr'), 'torch', importlib.metadata.version('torch'))
    for checkpoint in sorted((pathlib.Path.home() / '.roboflow' / 'models').glob('rf-detr-medium*.pth')):
        print(f'checkpoint {checkpoint.name} {checkpoint.stat().st_size} bytes sha256 {sha256(checkpoint)}')
    print(f'{target.name} {target.stat().st_size} bytes sha256 {sha256(target)}')


if __name__ == '__main__':
    main()
