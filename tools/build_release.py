"""Build the allowlisted update archive attached to each GitHub release."""
from pathlib import Path
import zipfile

root = Path(__file__).resolve().parent.parent
output = root / "outputs" / "court-creator-update.zip"
output.parent.mkdir(exist_ok=True)
with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
    for folder in ("electron", "court_creator"):
        for item in (root / folder).rglob("*"):
            if item.is_file() and "__pycache__" not in item.parts:
                archive.write(item, item.relative_to(root))
    for name in ("package.json", "updater.py", "Launch NBA 2K Court Creator.bat"):
        archive.write(root / name, name)
print(output)
