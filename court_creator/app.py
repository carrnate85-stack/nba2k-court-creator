from __future__ import annotations

import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import threading
import tkinter as tk
from tkinter import colorchooser, filedialog, messagebox, simpledialog, ttk

from . import __app_name__, __version__
from .court_template import (
    CourtLayer,
    CourtLayerDocument,
    create_court_preview_png,
    create_visible_court_preview_png,
    load_court_layer_state,
    parse_court_psd_layers,
    sample_template_layer_color,
    save_court_layer_state,
    warm_visible_preview_layers,
)


PROJECT_ROOT = Path(__file__).resolve().parent.parent
OUTPUT_DIR = PROJECT_ROOT / "outputs"
PREVIEW_CACHE = OUTPUT_DIR / "court_template_preview.png"
TEAM_PALETTES_PATH = PROJECT_ROOT / "data" / "team_palettes.json"
PRESETS_PATH = PROJECT_ROOT / "data" / "court_presets.json"
PRESET_COUNT = 5
CUSTOM_FLOORS_DIR = PROJECT_ROOT / "custom_floors"
CUSTOM_FLOORS_META = CUSTOM_FLOORS_DIR / "custom_floors.json"
PROJECT_COURT_TEMPLATE_PSD = (
    PROJECT_ROOT / "templates" / "NBA 2K25 Court Template By RedLite2K.psd"
)
DOWNLOAD_COURT_TEMPLATE_PSD = (
    Path.home()
    / "Downloads"
    / "NBA 2K26 -  Court Template - Jayderoza"
    / "NBA 2K26 -  Court Template - Jayderoza"
    / "NBA 2K25 Court Template By RedLite2K.psd"
)
DEFAULT_COURT_TEMPLATE_PSD = (
    PROJECT_COURT_TEMPLATE_PSD
    if PROJECT_COURT_TEMPLATE_PSD.exists()
    else DOWNLOAD_COURT_TEMPLATE_PSD
)


class CourtCreatorApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title(f"{__app_name__} {__version__}")
        self.geometry("1180x760")
        self.minsize(980, 620)

        self.template_path: Path | None = (
            DEFAULT_COURT_TEMPLATE_PSD if DEFAULT_COURT_TEMPLATE_PSD.exists() else None
        )
        self.layer_document: CourtLayerDocument | None = None
        self.layer_visibility: dict[str, bool] = {}
        self.layer_color_overrides: dict[str, tuple[int, int, int]] = {}
        self.layer_name_overrides: dict[str, str] = {}
        self.template_layer_colors: dict[str, tuple[int, int, int]] = {}
        self.custom_floor_layers: dict[str, CourtLayer] = {}
        self.custom_floor_images: dict[str, dict] = {}
        self.selected_layer_id: str | None = None
        self.preview_path: Path | None = PREVIEW_CACHE if PREVIEW_CACHE.exists() else None
        self.preview_image: tk.PhotoImage | None = None
        self.preview_rect: tuple[int, int, int, int] | None = None
        self._warmed_template_path: Path | None = None
        self.palette_swatch_images: dict[str, tk.PhotoImage] = {}
        self.team_palettes: list[dict] = self._load_team_palettes()
        self.presets: list[dict | None] = self._load_presets()
        self.preset_buttons: list[ttk.Button] = []

        self.layer_visible_var = tk.BooleanVar(value=True)
        self.layer_name_var = tk.StringVar(value="No court layer selected.")
        self.layer_detail_var = tk.StringVar(value="")
        self.selected_color_hex_var = tk.StringVar(value="")
        self.palette_search_var = tk.StringVar(value="")
        self.selected_color_swatch: tk.Label | None = None
        self.selected_color_entry: ttk.Entry | None = None
        self.selected_color_apply_button: ttk.Button | None = None
        self.selected_color_pick_button: ttk.Button | None = None
        self.selected_color_reset_button: ttk.Button | None = None

        self._configure_style()
        self._build_layout()
        self._refresh_palette_tree()
        self.after_idle(self.load_default_template)

    def _configure_style(self) -> None:
        self.style = ttk.Style(self)
        if "vista" in self.style.theme_names():
            self.style.theme_use("vista")
        self.style.configure("Title.TLabel", font=("Segoe UI", 16, "bold"))
        self.style.configure("Muted.TLabel", foreground="#5f6673")
        self.style.configure("Status.TLabel", foreground="#323842")

    def _build_layout(self) -> None:
        root = ttk.Frame(self, padding=16)
        root.pack(fill=tk.BOTH, expand=True)

        header = ttk.Frame(root)
        header.pack(fill=tk.X)
        title = ttk.Frame(header)
        title.pack(side=tk.LEFT, fill=tk.X, expand=True)
        ttk.Label(title, text="NBA 2K Court Creator", style="Title.TLabel").pack(
            anchor=tk.W
        )
        self.status = ttk.Label(
            title,
            text="Load the layered court PSD to inspect and manage its Photoshop layers.",
            style="Muted.TLabel",
        )
        self.status.pack(anchor=tk.W, pady=(4, 0))
        ttk.Button(header, text="Load PSD", command=self.load_template_psd).pack(
            side=tk.RIGHT
        )

        body = ttk.Frame(root)
        body.pack(fill=tk.BOTH, expand=True, pady=(14, 0))

        controls = ttk.Frame(body)
        controls.grid(row=0, column=0, sticky="nsew", padx=(0, 10))

        action_row = ttk.Frame(controls)
        action_row.grid(row=0, column=0, sticky="ew", pady=(0, 8))
        ttk.Button(action_row, text="Save State", command=self.save_state_as).grid(
            row=0, column=0, sticky="ew", padx=(0, 6), pady=(0, 6)
        )
        ttk.Button(action_row, text="Load State", command=self.load_state).grid(
            row=0, column=1, sticky="ew", padx=(0, 6), pady=(0, 6)
        )
        ttk.Button(action_row, text="Open PSD", command=self.open_template_psd).grid(
            row=0, column=2, sticky="ew", pady=(0, 6)
        )
        ttk.Button(action_row, text="Refresh Preview", command=self.refresh_preview).grid(
            row=1, column=0, sticky="ew", padx=(0, 6)
        )
        ttk.Button(action_row, text="Export PNG", command=self.export_flattened_png).grid(
            row=1, column=1, sticky="ew", padx=(0, 6)
        )
        ttk.Button(action_row, text="Show All", command=self.show_all_layers).grid(
            row=1, column=2, sticky="ew"
        )
        ttk.Button(action_row, text="Add Floor", command=self.add_custom_floor).grid(
            row=2, column=0, sticky="ew", pady=(6, 0), padx=(0, 6)
        )
        ttk.Button(action_row, text="Remove Floor", command=self.remove_custom_floor).grid(
            row=2, column=1, sticky="ew", pady=(6, 0), padx=(0, 6)
        )
        ttk.Button(action_row, text="Reset Default", command=self.reset_to_default).grid(
            row=2, column=2, sticky="ew", pady=(6, 0)
        )
        for column in range(3):
            action_row.columnconfigure(column, weight=1)

        self.layers = ttk.Treeview(
            controls,
            columns=("kind", "visible", "opacity"),
            show="tree headings",
            height=18,
        )
        self.layers.heading("#0", text="Layer")
        self.layers.heading("kind", text="Type")
        self.layers.heading("visible", text="Visible")
        self.layers.heading("opacity", text="Opacity")
        self.layers.column("#0", width=292, minwidth=220)
        self.layers.column("kind", width=66, anchor=tk.CENTER)
        self.layers.column("visible", width=62, anchor=tk.CENTER)
        self.layers.column("opacity", width=62, anchor=tk.E)
        self.layers.grid(row=1, column=0, sticky="nsew")
        self.layers.bind("<<TreeviewSelect>>", self._on_layer_select)
        self.layers.bind("<Double-1>", self._on_layer_double_click)
        self.layers.bind("<Button-3>", self._on_layer_right_click)
        layer_scroll = ttk.Scrollbar(controls, orient=tk.VERTICAL, command=self.layers.yview)
        layer_scroll.grid(row=1, column=1, sticky="ns")
        self.layers.configure(yscrollcommand=layer_scroll.set)

        selected = ttk.LabelFrame(controls, text="Selected layer", padding=8)
        selected.grid(row=2, column=0, sticky="ew", pady=(10, 0))
        ttk.Label(
            selected,
            textvariable=self.layer_name_var,
            style="Status.TLabel",
            wraplength=360,
        ).grid(row=0, column=0, columnspan=3, sticky="ew", pady=(0, 4))
        ttk.Label(
            selected,
            textvariable=self.layer_detail_var,
            style="Muted.TLabel",
            wraplength=360,
        ).grid(row=1, column=0, columnspan=3, sticky="ew", pady=(0, 8))
        selected_controls = ttk.Frame(selected)
        selected_controls.grid(row=2, column=0, columnspan=3, sticky="ew")
        ttk.Checkbutton(
            selected_controls,
            text="Visible",
            variable=self.layer_visible_var,
            command=self._on_layer_visible_changed,
        ).grid(row=0, column=0, sticky=tk.W)
        self.selected_color_swatch = tk.Label(
            selected_controls,
            width=4,
            height=1,
            relief=tk.SOLID,
            borderwidth=1,
            background="#f0f0f0",
        )
        self.selected_color_swatch.grid(row=0, column=1, sticky="ew", padx=(8, 4))
        self.selected_color_entry = ttk.Entry(
            selected_controls,
            textvariable=self.selected_color_hex_var,
            width=8,
        )
        self.selected_color_entry.grid(row=0, column=2, sticky="ew", padx=(4, 0))
        self.selected_color_entry.bind("<Return>", self.apply_selected_layer_hex_color)
        self.selected_color_entry.bind("<FocusOut>", self.apply_selected_layer_hex_color)
        self.selected_color_apply_button = ttk.Button(
            selected_controls,
            text="Apply",
            command=self.apply_selected_layer_hex_color,
        )
        self.selected_color_apply_button.grid(row=0, column=3, sticky="ew", padx=(6, 0))
        self.selected_color_pick_button = ttk.Button(
            selected_controls,
            text="Color",
            command=self.pick_selected_layer_color,
        )
        self.selected_color_pick_button.grid(row=0, column=4, sticky="ew", padx=(6, 0))
        self.selected_color_reset_button = ttk.Button(
            selected_controls,
            text="Reset",
            command=self.reset_selected_layer_color,
        )
        self.selected_color_reset_button.grid(row=0, column=5, sticky="ew", padx=(6, 0))
        ttk.Button(selected_controls, text="Toggle", command=self.toggle_selected_layer).grid(
            row=1,
            column=0,
            columnspan=3,
            sticky="ew",
            pady=(6, 0),
        )
        ttk.Button(selected_controls, text="Solo", command=self.solo_selected_layer).grid(
            row=1,
            column=3,
            columnspan=3,
            sticky="ew",
            padx=(6, 0),
            pady=(6, 0),
        )
        for column in range(6):
            selected_controls.columnconfigure(column, weight=1)
        selected.columnconfigure(0, weight=1)

        palette = ttk.LabelFrame(controls, text="Team colors", padding=8)
        palette.grid(row=3, column=0, sticky="nsew", pady=(10, 0))
        palette_search = ttk.Entry(palette, textvariable=self.palette_search_var)
        palette_search.grid(row=0, column=0, columnspan=2, sticky="ew", pady=(0, 6))
        palette_search.bind("<KeyRelease>", lambda _event: self._refresh_palette_tree())
        self.palette_tree = ttk.Treeview(
            palette,
            columns=("league", "hex"),
            show="tree headings",
            height=8,
        )
        self.palette_tree.heading("#0", text="Team / Color")
        self.palette_tree.heading("league", text="League")
        self.palette_tree.heading("hex", text="Hex")
        self.palette_tree.column("#0", width=250, minwidth=190)
        self.palette_tree.column("league", width=78, anchor=tk.CENTER)
        self.palette_tree.column("hex", width=78, anchor=tk.CENTER)
        self.palette_tree.grid(row=1, column=0, sticky="nsew")
        self.palette_tree.bind("<Double-1>", self.apply_selected_palette_color)
        palette_scroll = ttk.Scrollbar(
            palette,
            orient=tk.VERTICAL,
            command=self.palette_tree.yview,
        )
        palette_scroll.grid(row=1, column=1, sticky="ns")
        self.palette_tree.configure(yscrollcommand=palette_scroll.set)
        ttk.Button(
            palette,
            text="Apply Team Color",
            command=self.apply_selected_palette_color,
        ).grid(row=2, column=0, columnspan=2, sticky="ew", pady=(6, 0))
        palette.columnconfigure(0, weight=1)
        palette.rowconfigure(1, weight=1)

        preview_frame = ttk.Frame(body)
        preview_frame.grid(row=0, column=1, sticky="nsew")
        preset_row = ttk.Frame(preview_frame)
        preset_row.grid(row=0, column=0, sticky="ew", pady=(0, 6))
        for index in range(PRESET_COUNT):
            button = ttk.Button(
                preset_row,
                text=self._preset_button_label(index),
                command=lambda slot=index: self.load_preset(slot),
            )
            button.grid(row=0, column=index, sticky="ew", padx=(0, 6))
            button.bind(
                "<Button-3>",
                lambda event, slot=index: self.show_preset_menu(event, slot),
            )
            self.preset_buttons.append(button)
            preset_row.columnconfigure(index, weight=1)
        ttk.Label(preview_frame, text="Court Preview", style="Status.TLabel").grid(
            row=1,
            column=0,
            sticky=tk.W,
            pady=(0, 4),
        )
        self.preview_canvas = tk.Canvas(preview_frame, background="#20242b")
        self.preview_canvas.grid(row=2, column=0, sticky="nsew")
        self.preview_canvas.bind("<Configure>", lambda _event: self._show_preview())

        controls.columnconfigure(0, weight=1)
        controls.rowconfigure(1, weight=1)
        controls.rowconfigure(3, weight=1)
        preview_frame.columnconfigure(0, weight=1)
        preview_frame.rowconfigure(2, weight=1)
        body.columnconfigure(0, minsize=430)
        body.columnconfigure(1, weight=1)
        body.rowconfigure(0, weight=1)

    def load_default_template(self) -> None:
        if self.layer_document is not None or self.template_path is None:
            return
        self._load_template_path(self.template_path, show_errors=False)

    def load_template_psd(self) -> None:
        initial_dir = (
            self.template_path.parent
            if self.template_path is not None
            else Path.home() / "Downloads"
        )
        selected = filedialog.askopenfilename(
            title="Load Court PSD Template",
            initialdir=str(initial_dir),
            filetypes=(("Photoshop PSD", "*.psd"), ("All files", "*.*")),
        )
        if not selected:
            return
        self._load_template_path(Path(selected), show_errors=True)

    def _load_template_path(self, path: Path, *, show_errors: bool) -> None:
        try:
            document = parse_court_psd_layers(path)
        except (OSError, ValueError, struct.error) as exc:
            if show_errors:
                messagebox.showerror("Court PSD failed", str(exc))
            else:
                self.status.configure(text=f"Could not load default court PSD: {exc}")
            return

        self.template_path = Path(path)
        self.layer_document = document
        self.layer_visibility = {layer.id: layer.visible for layer in document.layers}
        self.layer_color_overrides = {}
        self.layer_name_overrides = {}
        self.template_layer_colors = {}
        self._load_custom_floor_layers()
        self.selected_layer_id = document.layers[0].id if document.layers else None
        self._refresh_selected_color_control()

        self._ensure_initial_preview(path)
        self._refresh_layer_tree()
        self._select_layer(self.selected_layer_id)

        self._show_preview()
        self._start_floor_preview_warmup()
        self.status.configure(
            text=(
                f"Loaded {path.name}: {len(document.layers)} selectable layers, "
                f"{document.width} x {document.height}."
            )
        )

    def _refresh_layer_tree(self) -> None:
        for item_id in self.layers.get_children():
            self.layers.delete(item_id)
        document = self.layer_document
        if document is None:
            return
        children: dict[str | None, list[CourtLayer]] = {}
        for layer in (*document.layers, *self.custom_floor_layers.values()):
            children.setdefault(layer.parent_id, []).append(layer)

        def insert_branch(parent_id: str | None) -> None:
            for layer in self._ordered_child_layers(children.get(parent_id, [])):
                self._insert_layer_tree_item(layer)
                if layer.kind == "group":
                    insert_branch(layer.id)

        insert_branch(None)

    def _insert_layer_tree_item(self, layer: CourtLayer) -> None:
        parent_id = layer.parent_id or ""
        visible = self.layer_visibility.get(layer.id, layer.visible)
        self.layers.insert(
            parent_id,
            tk.END,
            iid=layer.id,
            text=self._display_layer_name(layer),
            open=True,
            values=(
                self._layer_type_label(layer),
                "On" if visible else "Off",
                "" if self._is_custom_floor(layer) else f"{round(layer.opacity / 255 * 100)}%",
            ),
        )

    def _ordered_child_layers(self, layers: list[CourtLayer]) -> list[CourtLayer]:
        if layers and self._is_court_floor_group(self._layer_by_id(layers[0].parent_id)):
            return sorted(layers, key=self._court_floor_sort_key)
        if layers and self._is_lines_group(self._layer_by_id(layers[0].parent_id)):
            return sorted(layers, key=self._line_sort_key)
        return layers

    def _court_floor_sort_key(self, layer: CourtLayer) -> tuple[int, str]:
        if self._is_custom_floor(layer):
            return 10000, layer.name.casefold()
        match = re.search(r"#\s*(\d+)", layer.name)
        if match:
            return int(match.group(1)), layer.name.casefold()
        match = re.search(r"\d+", layer.name)
        if match:
            return int(match.group(0)), layer.name.casefold()
        return 9999, layer.name.casefold()

    def _is_court_floor_group(self, layer: CourtLayer | None) -> bool:
        return layer is not None and self._normalized_layer_name(layer.name) in {
            "court floors",
            "court floor",
            "floor options",
            "floors",
        }

    def _is_lines_group(self, layer: CourtLayer | None) -> bool:
        return layer is not None and self._normalized_layer_name(layer.name) == "lines"

    def _line_sort_key(self, layer: CourtLayer) -> tuple[int, int, str]:
        priority = {
            "3 point lines": 0,
            "college three": 1,
            "high school three": 2,
        }
        normalized = self._normalized_layer_name(layer.name)
        return (
            priority.get(normalized, 10),
            layer.psd_index,
            self._display_layer_name(layer).casefold(),
        )

    def _display_layer_name(self, layer: CourtLayer) -> str:
        if layer.id in self.layer_name_overrides:
            return self.layer_name_overrides[layer.id]
        display_names = {
            "3 point lines": "NBA Three",
            "college three": "College Three",
            "high school three": "High School Three",
        }
        return display_names.get(self._normalized_layer_name(layer.name), layer.name)

    def _layer_type_label(self, layer: CourtLayer) -> str:
        if self._is_custom_floor(layer):
            return "Floor"
        return "Group" if layer.kind == "group" else "Layer"

    def _is_custom_floor(self, layer: CourtLayer | None) -> bool:
        return layer is not None and layer.id in self.custom_floor_layers

    def _is_court_floor_option(self, layer: CourtLayer) -> bool:
        floor_group = self._court_floor_group_for(layer)
        if floor_group is None or floor_group.id == layer.id:
            return False
        return self._floor_option_root(layer, floor_group) is not None

    def _on_layer_select(self, _event: tk.Event | None = None) -> None:
        selected = self.layers.selection()
        self.selected_layer_id = selected[0] if selected else None
        layer = self._selected_layer()
        if layer is None:
            self.layer_name_var.set("No court layer selected.")
            self.layer_detail_var.set("")
            self._refresh_selected_color_control()
            self._show_preview()
            return

        visible = self.layer_visibility.get(layer.id, layer.visible)
        self.layer_visible_var.set(visible)
        self.layer_name_var.set(self._display_layer_name(layer))
        self.layer_detail_var.set(
            (
                f"{'Group' if layer.kind == 'group' else 'Layer'} | "
                f"Opacity {round(layer.opacity / 255 * 100)}%"
            )
        )
        self._refresh_selected_color_control()
        self._show_preview()

    def _on_layer_double_click(self, event: tk.Event) -> None:
        item_id = self.layers.identify_row(event.y)
        if not item_id:
            return
        layer = self._layer_by_id(item_id)
        if layer is None:
            return
        self.layers.selection_set(item_id)
        self.layers.focus(item_id)
        self.selected_layer_id = item_id
        self._on_layer_select()
        self._activate_layer_from_click(layer)

    def _on_layer_right_click(self, event: tk.Event) -> None:
        item_id = self.layers.identify_row(event.y)
        if not item_id:
            return
        self.layers.selection_set(item_id)
        self.layers.focus(item_id)
        self.selected_layer_id = item_id
        self._on_layer_select()
        self.rename_selected_layer()

    def rename_selected_layer(self) -> None:
        layer = self._selected_layer()
        if layer is None:
            return
        current_name = self._display_layer_name(layer)
        new_name = simpledialog.askstring(
            "Rename Layer",
            "Layer name:",
            initialvalue=current_name,
            parent=self,
        )
        if new_name is None:
            return
        new_name = " ".join(new_name.strip().split())
        if not new_name:
            return
        self._rename_layer(layer, new_name)
        self._refresh_layer_tree()
        self._select_layer(layer.id)
        self.status.configure(text=f"Renamed {current_name} to {new_name}.")

    def _activate_layer_from_click(self, layer: CourtLayer) -> None:
        if self._is_court_floor_option(layer):
            current = self.layer_visibility.get(layer.id, layer.visible)
            self.layer_visibility[layer.id] = not current
            if not current:
                self._show_ancestor_layers(layer)
                self._hide_other_court_floors(layer)
            self._refresh_layer_view(layer.id)
            return
        current = self.layer_visibility.get(layer.id, layer.visible)
        self.layer_visibility[layer.id] = not current
        if not current:
            self._show_ancestor_layers(layer)
        self._refresh_layer_view(layer.id)

    def _on_layer_visible_changed(self) -> None:
        layer = self._selected_layer()
        if layer is None:
            return
        visible = bool(self.layer_visible_var.get())
        self.layer_visibility[layer.id] = visible
        if visible:
            self._show_ancestor_layers(layer)
            self._hide_other_court_floors(layer)
        self._refresh_layer_view(layer.id)
        self.status.configure(
            text=f"Updated layer state for {layer.name}. Save State keeps this setup."
        )

    def toggle_selected_layer(self) -> None:
        layer = self._selected_layer()
        if layer is None:
            return
        current = self.layer_visibility.get(layer.id, layer.visible)
        visible = not current
        self.layer_visibility[layer.id] = visible
        self.layer_visible_var.set(visible)
        if visible:
            self._show_ancestor_layers(layer)
            self._hide_other_court_floors(layer)
        self._refresh_layer_view(layer.id)

    def solo_selected_layer(self) -> None:
        layer = self._selected_layer()
        document = self.layer_document
        if layer is None or document is None:
            return
        visible_ids = {layer.id}
        if layer.kind == "group":
            visible_ids.update(self._descendant_layer_ids(layer.id))
        parent_id = layer.parent_id
        while parent_id:
            visible_ids.add(parent_id)
            parent = self._layer_by_id(parent_id)
            parent_id = parent.parent_id if parent else None
        self.layer_visibility = {
            item.id: item.id in visible_ids for item in document.layers
        }
        self._refresh_layer_view(layer.id)

    def show_all_layers(self) -> None:
        document = self.layer_document
        if document is None:
            return
        self.layer_visibility = {
            layer.id: True for layer in (*document.layers, *self.custom_floor_layers.values())
        }
        selected_id = self.selected_layer_id
        self._refresh_layer_view(selected_id)

    def reset_to_default(self) -> None:
        document = self.layer_document
        if document is None:
            messagebox.showinfo("Court Creator", "Load a court PSD first.")
            return
        self.layer_visibility = {layer.id: layer.visible for layer in document.layers}
        for layer_id in self.custom_floor_layers:
            self.layer_visibility[layer_id] = False
        self.layer_color_overrides = {}
        self.layer_name_overrides = {}
        self.template_layer_colors = {}
        self.selected_layer_id = document.layers[0].id if document.layers else None
        self._refresh_selected_color_control()
        self._refresh_layer_view(self.selected_layer_id)
        self.status.configure(text="Reset court to the template default.")

    def add_custom_floor(self) -> None:
        if self.layer_document is None:
            messagebox.showinfo("Court Creator", "Load a court PSD first.")
            return
        floor_group = self._court_floor_group()
        floor_bbox = self._court_floor_bbox()
        if floor_group is None or floor_bbox is None:
            messagebox.showerror("Court Creator", "Could not find the Court Floors group.")
            return

        selected = filedialog.askopenfilename(
            title="Add Custom Court Floor",
            filetypes=(
                ("Image files", "*.png *.jpg *.jpeg *.webp *.bmp"),
                ("All files", "*.*"),
            ),
        )
        if not selected:
            return

        source = Path(selected)
        try:
            self._validate_custom_floor_image(source)
            layer = self._copy_custom_floor(source, floor_group.id, floor_bbox)
            self.layer_visibility[layer.id] = True
            self._show_ancestor_layers(layer)
            self._hide_other_court_floors(layer)
            self._save_custom_floor_metadata()
        except (OSError, RuntimeError, ValueError) as exc:
            messagebox.showerror("Add Custom Floor failed", str(exc))
            return

        self._refresh_layer_view(layer.id)
        self.status.configure(text=f"Added custom floor: {layer.name}.")

    def remove_custom_floor(self) -> None:
        layer = self._selected_layer()
        if layer is None or not self._is_custom_floor(layer):
            messagebox.showinfo("Court Creator", "Select a custom floor first.")
            return

        if not messagebox.askyesno(
            "Remove Custom Floor",
            f"Remove {layer.name} from the floor picker?",
            parent=self,
        ):
            return

        was_visible = self.layer_visibility.get(layer.id, False)
        image_info = self.custom_floor_images.get(layer.id, {})
        image_path = self._resolve_custom_floor_path(str(image_info.get("path", "")))

        self.custom_floor_layers.pop(layer.id, None)
        self.custom_floor_images.pop(layer.id, None)
        self.layer_visibility.pop(layer.id, None)
        self.layer_color_overrides.pop(layer.id, None)
        self.template_layer_colors.pop(layer.id, None)
        if self.selected_layer_id == layer.id:
            self.selected_layer_id = self._first_court_floor_id()

        try:
            if image_path.exists() and image_path.is_relative_to(CUSTOM_FLOORS_DIR):
                image_path.unlink()
            self._save_custom_floor_metadata()
        except OSError as exc:
            messagebox.showerror("Remove Custom Floor failed", str(exc))
            return

        if was_visible and self.selected_layer_id:
            selected = self._layer_by_id(self.selected_layer_id)
            if selected is not None:
                self.layer_visibility[selected.id] = True
                self._show_ancestor_layers(selected)
                self._hide_other_court_floors(selected)

        self._refresh_layer_view(self.selected_layer_id)
        self.status.configure(text=f"Removed custom floor: {layer.name}.")

    def pick_selected_layer_color(self) -> None:
        layer = self._selected_color_layer()
        if layer is None:
            return
        initial_color = self._layer_color_hex(layer) or "#ffffff"
        _rgb, hex_color = colorchooser.askcolor(
            color=initial_color,
            title=f"Pick {self._display_layer_name(layer)} Color",
            parent=self,
        )
        if not hex_color:
            return
        color = self._hex_to_rgb(hex_color)
        self.layer_color_overrides[layer.id] = color
        self._refresh_selected_color_control()
        self._refresh_layer_view(layer.id)
        self.status.configure(text=f"Updated {self._display_layer_name(layer)} color.")

    def apply_selected_layer_hex_color(self, _event: tk.Event | None = None) -> None:
        layer = self._selected_color_layer()
        if layer is None:
            return
        hex_color = self.selected_color_hex_var.get().strip()
        color = self._parse_hex_color(hex_color)
        if color is None:
            self._refresh_selected_color_control()
            self.status.configure(text="Enter a color like #552583 or FFFFFF.")
            return
        self.layer_color_overrides[layer.id] = color
        self._refresh_selected_color_control()
        self._refresh_layer_view(layer.id)
        self.status.configure(text=f"Updated {self._display_layer_name(layer)} color.")

    def reset_selected_layer_color(self) -> None:
        layer = self._selected_color_layer()
        if layer is None:
            return
        self.layer_color_overrides.pop(layer.id, None)
        self._refresh_selected_color_control()
        self._refresh_layer_view(layer.id)
        self.status.configure(text=f"Reset {self._display_layer_name(layer)} color.")

    def apply_selected_palette_color(self, _event: tk.Event | None = None) -> None:
        layer = self._selected_color_layer()
        if layer is None:
            self.status.configure(text="Select an outside, paint, or line layer first.")
            return
        palette_color = self._selected_palette_color()
        if palette_color is None:
            self.status.configure(text="Pick a team color from the palette first.")
            return
        team_name, color_name, hex_color = palette_color
        color = self._parse_hex_color(hex_color)
        if color is None:
            return
        self.layer_color_overrides[layer.id] = color
        self._refresh_selected_color_control()
        self._refresh_layer_view(layer.id)
        self.status.configure(
            text=(
                f"Applied {team_name} {color_name} "
                f"to {self._display_layer_name(layer)}."
            )
        )

    def load_preset(self, slot: int) -> None:
        preset = self.presets[slot] if 0 <= slot < len(self.presets) else None
        document = self.layer_document
        if document is None:
            messagebox.showinfo("Court Creator", "Load a court PSD first.")
            return
        if not self._preset_has_layout(slot):
            self.status.configure(text=f"Preset {slot + 1} is empty. Right-click to save it.")
            return

        self.layer_visibility = {layer.id: layer.visible for layer in document.layers}
        for layer_id in self.custom_floor_layers:
            self.layer_visibility[layer_id] = False

        saved_visibility = preset.get("visibility", {})
        if isinstance(saved_visibility, dict):
            for layer_id, visible in saved_visibility.items():
                self.layer_visibility[str(layer_id)] = bool(visible)

        self.layer_color_overrides = self._normalize_color_overrides(
            preset.get("color_overrides", {})
        )
        self.layer_name_overrides = {
            str(layer_id): str(name)
            for layer_id, name in preset.get("name_overrides", {}).items()
            if str(name).strip()
        }
        self.template_layer_colors = {}
        selected_layer_id = str(preset.get("selected_layer_id") or "")
        self.selected_layer_id = selected_layer_id if self._layer_by_id(selected_layer_id) else (
            document.layers[0].id if document.layers else None
        )
        self._refresh_selected_color_control()
        self._refresh_layer_view(self.selected_layer_id)
        self.status.configure(text=f"Loaded {self._preset_name(slot)}.")

    def show_preset_menu(self, event: tk.Event, slot: int) -> str:
        menu = tk.Menu(self, tearoff=0)
        menu.add_command(
            label="Save Preset",
            command=lambda: self.save_current_layout_to_preset(slot),
        )
        menu.add_command(
            label="Rename",
            command=lambda: self.rename_preset(slot),
        )
        menu.tk_popup(event.x_root, event.y_root)
        return "break"

    def rename_preset(self, slot: int) -> None:
        current_name = self._preset_name(slot)
        preset_name = simpledialog.askstring(
            "Rename Preset",
            "Preset name:",
            initialvalue=current_name,
            parent=self,
        )
        if preset_name is None:
            return
        preset_name = " ".join(preset_name.strip().split())
        if not preset_name:
            return
        if self.presets[slot] is None:
            self.presets[slot] = {"name": preset_name}
        else:
            self.presets[slot]["name"] = preset_name
        self._save_presets()
        self._refresh_preset_buttons()
        self.status.configure(text=f"Renamed preset to {preset_name}.")

    def save_current_layout_to_preset(self, slot: int) -> None:
        document = self.layer_document
        if document is None:
            messagebox.showinfo("Court Creator", "Load a court PSD first.")
            return
        preset_name = self._preset_name(slot)
        self.presets[slot] = {
            "name": preset_name,
            "template_path": str(self.template_path or document.path),
            "visibility": dict(self.layer_visibility),
            "color_overrides": self._serializable_color_overrides(),
            "name_overrides": dict(self.layer_name_overrides),
            "selected_layer_id": self.selected_layer_id,
        }
        self._save_presets()
        self._refresh_preset_buttons()
        self.status.configure(text=f"Saved current layout to {preset_name}.")

    def save_state_as(self) -> None:
        document = self.layer_document
        if document is None:
            messagebox.showinfo("Court Creator", "Load a court PSD first.")
            return
        selected = filedialog.asksaveasfilename(
            title="Save Court Layer State",
            defaultextension=".json",
            filetypes=(("Court state JSON", "*.json"), ("All files", "*.*")),
        )
        if not selected:
            return
        try:
            save_court_layer_state(
                Path(selected),
                document,
                self.layer_visibility,
                self.selected_layer_id,
                self.layer_color_overrides,
                self.layer_name_overrides,
            )
        except OSError as exc:
            messagebox.showerror("Save Court State failed", str(exc))
            return
        self.status.configure(text=f"Saved court layer state to {Path(selected).name}.")

    def load_state(self) -> None:
        selected = filedialog.askopenfilename(
            title="Load Court Layer State",
            filetypes=(("Court state JSON", "*.json"), ("All files", "*.*")),
        )
        if not selected:
            return
        try:
            loaded_state = load_court_layer_state(Path(selected))
            document, visibility, selected_layer_id, color_overrides = loaded_state[:4]
            name_overrides = loaded_state[4] if len(loaded_state) > 4 else {}
        except (OSError, ValueError, TypeError, json.JSONDecodeError) as exc:
            messagebox.showerror("Load Court State failed", str(exc))
            return
        self.layer_document = document
        self.template_path = Path(document.path) if document.path else None
        self._load_custom_floor_layers()
        self.layer_visibility = visibility or {
            layer.id: layer.visible for layer in document.layers
        }
        self.layer_color_overrides = color_overrides
        self.layer_name_overrides = name_overrides
        self.template_layer_colors = {}
        self.selected_layer_id = selected_layer_id
        self._refresh_selected_color_control()
        self._render_visible_preview(show_errors=False)
        self._refresh_layer_tree()
        self._select_layer(selected_layer_id)
        self._show_preview()
        self._start_floor_preview_warmup()
        self.status.configure(text=f"Loaded court layer state from {Path(selected).name}.")

    def refresh_preview(self) -> None:
        if self.template_path is None:
            messagebox.showinfo("Court Creator", "Load a court PSD first.")
            return
        if not self._render_visible_preview(show_errors=True):
            return
        self._show_preview()
        self.status.configure(text="Refreshed the visible-layer court preview.")

    def export_flattened_png(self) -> None:
        if self.template_path is None:
            messagebox.showinfo("Court Creator", "Load a court PSD first.")
            return
        selected = filedialog.asksaveasfilename(
            title="Export Flattened Court PNG",
            defaultextension=".png",
            filetypes=(("PNG image", "*.png"), ("All files", "*.*")),
        )
        if not selected:
            return
        try:
            create_court_preview_png(self.template_path, Path(selected), max_size=None)
        except (OSError, RuntimeError) as exc:
            messagebox.showerror("Export Court PNG failed", str(exc))
            return
        self.status.configure(text=f"Exported flattened court PNG to {Path(selected).name}.")

    def open_template_psd(self) -> None:
        if self.template_path is None:
            messagebox.showinfo("Court Creator", "Load a court PSD first.")
            return
        photoshop = find_photoshop_executable()
        if photoshop:
            subprocess.Popen([str(photoshop), str(self.template_path)])
            return
        os.startfile(self.template_path)

    def _selected_layer(self) -> CourtLayer | None:
        return self._layer_by_id(self.selected_layer_id)

    def _layer_by_id(self, layer_id: str | None) -> CourtLayer | None:
        document = self.layer_document
        if layer_id is None:
            return None
        if layer_id in self.custom_floor_layers:
            return self.custom_floor_layers[layer_id]
        if document is None:
            return None
        return next((layer for layer in document.layers if layer.id == layer_id), None)

    def _court_floor_group(self) -> CourtLayer | None:
        document = self.layer_document
        if document is None:
            return None
        return next(
            (layer for layer in document.layers if self._is_court_floor_group(layer)),
            None,
        )

    def _court_floor_bbox(self) -> tuple[int, int, int, int] | None:
        document = self.layer_document
        floor_group = self._court_floor_group()
        if document is None or floor_group is None:
            return None
        floor_layers = [
            layer
            for layer in document.layers
            if layer.parent_id == floor_group.id and layer.kind == "layer"
        ]
        floor_layers = [layer for layer in floor_layers if layer.bbox[2] > 0 and layer.bbox[3] > 0]
        if not floor_layers:
            return None
        return floor_layers[0].bbox

    def _first_court_floor_id(self) -> str | None:
        document = self.layer_document
        floor_group = self._court_floor_group()
        if document is None or floor_group is None:
            return None
        floors = [
            layer
            for layer in document.layers
            if layer.parent_id == floor_group.id and layer.kind == "layer"
        ]
        ordered = self._ordered_child_layers(floors)
        return ordered[0].id if ordered else None

    def _validate_custom_floor_image(self, path: Path) -> None:
        try:
            from PIL import Image
        except ImportError as exc:
            raise RuntimeError("Adding custom floors requires Pillow.") from exc
        with Image.open(path) as image:
            image.verify()

    def _copy_custom_floor(
        self,
        source: Path,
        parent_id: str,
        bbox: tuple[int, int, int, int],
    ) -> CourtLayer:
        CUSTOM_FLOORS_DIR.mkdir(parents=True, exist_ok=True)
        stem = self._safe_custom_floor_stem(source.stem)
        suffix = source.suffix.lower() or ".png"
        destination = CUSTOM_FLOORS_DIR / f"{stem}{suffix}"
        counter = 2
        while destination.exists():
            destination = CUSTOM_FLOORS_DIR / f"{stem}-{counter}{suffix}"
            counter += 1
        shutil.copy2(source, destination)

        layer_id = f"custom_floor_{destination.stem}"
        layer = CourtLayer(
            id=layer_id,
            name=destination.stem.replace("-", " ").replace("_", " ").title(),
            kind="layer",
            parent_id=parent_id,
            psd_index=10000 + len(self.custom_floor_layers),
            depth=1,
            visible=False,
            opacity=255,
            blend_mode="norm",
            bbox=bbox,
        )
        self.custom_floor_layers[layer.id] = layer
        self.custom_floor_images[layer.id] = {
            "id": layer.id,
            "name": layer.name,
            "path": str(destination.relative_to(PROJECT_ROOT)),
            "bbox": bbox,
        }
        return layer

    def _rename_layer(self, layer: CourtLayer, new_name: str) -> None:
        if self._is_custom_floor(layer):
            renamed = CourtLayer(
                id=layer.id,
                name=new_name,
                kind=layer.kind,
                parent_id=layer.parent_id,
                psd_index=layer.psd_index,
                depth=layer.depth,
                visible=layer.visible,
                opacity=layer.opacity,
                blend_mode=layer.blend_mode,
                bbox=layer.bbox,
                divider_type=layer.divider_type,
            )
            self.custom_floor_layers[layer.id] = renamed
            if layer.id in self.custom_floor_images:
                self.custom_floor_images[layer.id]["name"] = new_name
            self._save_custom_floor_metadata()
            self.layer_name_overrides.pop(layer.id, None)
        else:
            self.layer_name_overrides[layer.id] = new_name

    def _safe_custom_floor_stem(self, value: str) -> str:
        safe = re.sub(r"[^A-Za-z0-9_-]+", "-", value.strip()).strip("-_").lower()
        return safe or "custom-floor"

    def _load_custom_floor_layers(self) -> None:
        self.custom_floor_layers = {}
        self.custom_floor_images = {}
        floor_group = self._court_floor_group()
        fallback_bbox = self._court_floor_bbox()
        if floor_group is None or fallback_bbox is None or not CUSTOM_FLOORS_META.exists():
            return
        try:
            data = json.loads(CUSTOM_FLOORS_META.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return
        for index, item in enumerate(data.get("floors", [])):
            path = self._resolve_custom_floor_path(str(item.get("path", "")))
            if not path.exists():
                continue
            bbox = tuple(item.get("bbox", fallback_bbox))
            if len(bbox) != 4:
                bbox = fallback_bbox
            layer_id = str(item.get("id") or f"custom_floor_{path.stem}")
            layer = CourtLayer(
                id=layer_id,
                name=str(item.get("name") or path.stem),
                kind="layer",
                parent_id=floor_group.id,
                psd_index=10000 + index,
                depth=1,
                visible=False,
                opacity=255,
                blend_mode="norm",
                bbox=tuple(int(value) for value in bbox),
            )
            self.custom_floor_layers[layer.id] = layer
            self.custom_floor_images[layer.id] = {
                "id": layer.id,
                "name": layer.name,
                "path": str(path.relative_to(PROJECT_ROOT)) if path.is_relative_to(PROJECT_ROOT) else str(path),
                "bbox": layer.bbox,
            }

    def _save_custom_floor_metadata(self) -> None:
        CUSTOM_FLOORS_DIR.mkdir(parents=True, exist_ok=True)
        data = {"floors": list(self.custom_floor_images.values())}
        CUSTOM_FLOORS_META.write_text(json.dumps(data, indent=2), encoding="utf-8")

    def _resolve_custom_floor_path(self, value: str) -> Path:
        path = Path(value)
        return path if path.is_absolute() else PROJECT_ROOT / path

    def _load_presets(self) -> list[dict | None]:
        presets: list[dict | None] = [None for _ in range(PRESET_COUNT)]
        if not PRESETS_PATH.exists():
            return presets
        try:
            data = json.loads(PRESETS_PATH.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return presets
        saved_presets = data.get("presets", []) if isinstance(data, dict) else []
        if not isinstance(saved_presets, list):
            return presets
        for index, preset in enumerate(saved_presets[:PRESET_COUNT]):
            presets[index] = preset if isinstance(preset, dict) else None
        return presets

    def _save_presets(self) -> None:
        PRESETS_PATH.parent.mkdir(parents=True, exist_ok=True)
        data = {"presets": self.presets}
        PRESETS_PATH.write_text(json.dumps(data, indent=2), encoding="utf-8")

    def _preset_name(self, slot: int) -> str:
        preset = self.presets[slot] if 0 <= slot < len(self.presets) else None
        if isinstance(preset, dict):
            name = str(preset.get("name", "")).strip()
            if name:
                return name
        return f"Preset {slot + 1}"

    def _preset_has_layout(self, slot: int) -> bool:
        preset = self.presets[slot] if 0 <= slot < len(self.presets) else None
        return isinstance(preset, dict) and isinstance(preset.get("visibility"), dict)

    def _preset_button_label(self, slot: int) -> str:
        return self._preset_name(slot)

    def _refresh_preset_buttons(self) -> None:
        for index, button in enumerate(self.preset_buttons):
            button.configure(text=self._preset_button_label(index))

    def _serializable_color_overrides(self) -> dict[str, list[int]]:
        return {
            str(layer_id): [int(color[0]), int(color[1]), int(color[2])]
            for layer_id, color in self.layer_color_overrides.items()
        }

    def _normalize_color_overrides(self, color_overrides: object) -> dict[str, tuple[int, int, int]]:
        if not isinstance(color_overrides, dict):
            return {}
        normalized: dict[str, tuple[int, int, int]] = {}
        for layer_id, value in color_overrides.items():
            if not isinstance(value, list | tuple) or len(value) < 3:
                continue
            normalized[str(layer_id)] = tuple(
                max(0, min(255, int(channel))) for channel in value[:3]
            )
        return normalized

    def _load_team_palettes(self) -> list[dict]:
        if not TEAM_PALETTES_PATH.exists():
            return []
        try:
            data = json.loads(TEAM_PALETTES_PATH.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return []
        palettes = data.get("palettes", data if isinstance(data, list) else [])
        return palettes if isinstance(palettes, list) else []

    def _refresh_palette_tree(self) -> None:
        if not hasattr(self, "palette_tree"):
            return
        for item_id in self.palette_tree.get_children():
            self.palette_tree.delete(item_id)

        query = self.palette_search_var.get().strip().casefold()
        shown = 0
        for entry_index, palette in enumerate(self.team_palettes):
            team_name = str(palette.get("team", "")).strip()
            league = str(palette.get("league", "")).strip()
            colors = palette.get("colors", [])
            if not team_name or not isinstance(colors, list):
                continue
            matching_colors = []
            for color_index, color in enumerate(colors):
                color_name = str(color.get("name", "")).strip()
                hex_color = str(color.get("hex", "")).strip().upper()
                haystack = " ".join((team_name, league, color_name, hex_color)).casefold()
                if query and query not in haystack:
                    continue
                matching_colors.append((color_index, color_name, hex_color))
            if not matching_colors:
                continue

            parent_id = f"palette_team:{entry_index}"
            self.palette_tree.insert(
                "",
                tk.END,
                iid=parent_id,
                text=team_name,
                open=bool(query),
                values=(league, ""),
            )
            for color_index, color_name, hex_color in matching_colors:
                item_id = f"palette_color:{entry_index}:{color_index}"
                self.palette_tree.insert(
                    parent_id,
                    tk.END,
                    iid=item_id,
                    text=color_name or hex_color,
                    image=self._palette_swatch(hex_color),
                    values=(league, hex_color),
                )
            shown += 1
            if shown >= 80:
                break

    def _selected_palette_color(self) -> tuple[str, str, str] | None:
        selected = self.palette_tree.selection() if hasattr(self, "palette_tree") else ()
        if not selected:
            return None
        item_id = selected[0]
        if item_id.startswith("palette_team:"):
            children = self.palette_tree.get_children(item_id)
            if not children:
                return None
            item_id = children[0]
        parts = item_id.split(":")
        if len(parts) != 3 or parts[0] != "palette_color":
            return None
        try:
            entry_index = int(parts[1])
            color_index = int(parts[2])
            palette = self.team_palettes[entry_index]
            color = palette["colors"][color_index]
        except (IndexError, KeyError, TypeError, ValueError):
            return None
        return (
            str(palette.get("team", "")).strip(),
            str(color.get("name", "")).strip(),
            str(color.get("hex", "")).strip(),
        )

    def _palette_swatch(self, hex_color: str) -> tk.PhotoImage:
        color = hex_color if self._parse_hex_color(hex_color) is not None else "#ffffff"
        if color in self.palette_swatch_images:
            return self.palette_swatch_images[color]
        image = tk.PhotoImage(width=18, height=14)
        image.put(color, to=(0, 0, 18, 14))
        image.put("#20242b", to=(0, 0, 18, 1))
        image.put("#20242b", to=(0, 13, 18, 14))
        image.put("#20242b", to=(0, 0, 1, 14))
        image.put("#20242b", to=(17, 0, 18, 14))
        self.palette_swatch_images[color] = image
        return image

    def _descendant_layer_ids(self, layer_id: str) -> set[str]:
        document = self.layer_document
        if document is None:
            return set()
        descendants: set[str] = set()
        changed = True
        while changed:
            changed = False
            for layer in document.layers:
                if layer.parent_id == layer_id or layer.parent_id in descendants:
                    if layer.id not in descendants:
                        descendants.add(layer.id)
                        changed = True
        return descendants

    def _ancestor_layers(self, layer: CourtLayer) -> list[CourtLayer]:
        ancestors: list[CourtLayer] = []
        parent_id = layer.parent_id
        while parent_id:
            parent = self._layer_by_id(parent_id)
            if parent is None:
                break
            ancestors.append(parent)
            parent_id = parent.parent_id
        return ancestors

    def _court_floor_group_for(self, layer: CourtLayer) -> CourtLayer | None:
        candidates = [layer, *self._ancestor_layers(layer)]
        for candidate in candidates:
            normalized = candidate.name.casefold().replace("_", " ").replace("-", " ")
            words = " ".join(normalized.split())
            if words in {"court floors", "court floor", "floor options", "floors"}:
                return candidate
        return None

    def _floor_option_root(self, layer: CourtLayer, floor_group: CourtLayer) -> CourtLayer | None:
        root = layer
        while root.parent_id and root.parent_id != floor_group.id:
            parent = self._layer_by_id(root.parent_id)
            if parent is None:
                return None
            root = parent
        return root if root.parent_id == floor_group.id else None

    def _selected_color_layer(self) -> CourtLayer | None:
        layer = self._selected_layer()
        if layer is None or layer.kind != "layer":
            return None
        if self._is_colorable_layer(layer):
            return layer
        return None

    def _is_colorable_layer(self, layer: CourtLayer) -> bool:
        if self._normalized_layer_name(layer.name) == "outside color":
            return True
        return any(
            self._normalized_layer_name(parent.name) in {"paint colors", "lines"}
            for parent in self._ancestor_layers(layer)
        )

    def _layer_color_hex(self, layer: CourtLayer) -> str | None:
        color = self.layer_color_overrides.get(layer.id)
        if color is not None:
            return self._rgb_to_hex(color)
        color = self._template_layer_color(layer)
        if color is not None:
            return self._rgb_to_hex(color)
        return None

    def _template_layer_color(self, layer: CourtLayer) -> tuple[int, int, int] | None:
        if layer.id in self.template_layer_colors:
            return self.template_layer_colors[layer.id]
        if self.template_path is None or self.layer_document is None:
            return None
        try:
            color = sample_template_layer_color(
                self.template_path,
                self.layer_document,
                layer.id,
            )
        except (OSError, RuntimeError, ValueError, struct.error):
            color = None
        if color is not None:
            self.template_layer_colors[layer.id] = color
        return color

    def _refresh_selected_color_control(self) -> None:
        layer = self._selected_color_layer()
        enabled = layer is not None
        swatch_color = self._layer_color_hex(layer) if layer is not None else None
        if self.selected_color_swatch is not None:
            self.selected_color_swatch.configure(
                background=swatch_color or ("#ffffff" if enabled else "#f0f0f0")
            )
        self.selected_color_hex_var.set(swatch_color or "")
        state = tk.NORMAL if enabled else tk.DISABLED
        if self.selected_color_entry is not None:
            self.selected_color_entry.configure(state=state)
        if self.selected_color_apply_button is not None:
            self.selected_color_apply_button.configure(state=state)
        if self.selected_color_pick_button is not None:
            self.selected_color_pick_button.configure(state=state)
        if self.selected_color_reset_button is not None:
            self.selected_color_reset_button.configure(state=state)

    def _normalized_layer_name(self, name: str) -> str:
        return " ".join(name.casefold().replace("_", " ").replace("-", " ").split())

    def _rgb_to_hex(self, color: tuple[int, int, int]) -> str:
        return f"#{color[0]:02x}{color[1]:02x}{color[2]:02x}"

    def _hex_to_rgb(self, hex_color: str) -> tuple[int, int, int]:
        value = hex_color.lstrip("#")
        return tuple(int(value[index : index + 2], 16) for index in (0, 2, 4))

    def _parse_hex_color(self, hex_color: str) -> tuple[int, int, int] | None:
        value = hex_color.strip().lstrip("#")
        if len(value) == 3:
            value = "".join(character * 2 for character in value)
        if len(value) != 6 or any(character not in "0123456789abcdefABCDEF" for character in value):
            return None
        return tuple(int(value[index : index + 2], 16) for index in (0, 2, 4))

    def _hide_other_court_floors(self, layer: CourtLayer) -> None:
        floor_group = self._court_floor_group_for(layer)
        if floor_group is None or floor_group.id == layer.id:
            return
        selected_root = self._floor_option_root(layer, floor_group)
        if selected_root is None:
            return

        selected_ids = {selected_root.id, *self._descendant_layer_ids(selected_root.id)}
        for item in self._court_floor_options(floor_group):
            if item.parent_id != floor_group.id or item.id == selected_root.id:
                continue
            for hidden_id in {item.id, *self._descendant_layer_ids(item.id)}:
                self.layer_visibility[hidden_id] = False

        self.layer_visibility[floor_group.id] = True
        for ancestor in self._ancestor_layers(floor_group):
            self.layer_visibility[ancestor.id] = True
        for selected_id in selected_ids:
            self.layer_visibility[selected_id] = True

    def _court_floor_options(self, floor_group: CourtLayer) -> list[CourtLayer]:
        document_layers = self.layer_document.layers if self.layer_document else ()
        return [
            layer
            for layer in (*document_layers, *self.custom_floor_layers.values())
            if layer.parent_id == floor_group.id
        ]

    def _show_ancestor_layers(self, layer: CourtLayer) -> None:
        for ancestor in self._ancestor_layers(layer):
            self.layer_visibility[ancestor.id] = True

    def _refresh_layer_view(self, selected_layer_id: str | None) -> None:
        self._refresh_layer_tree()
        self._render_visible_preview(show_errors=False)
        self._select_layer(selected_layer_id)

    def _ensure_initial_preview(self, path: Path) -> None:
        if self.preview_path is not None and self.preview_path.exists():
            return
        try:
            create_court_preview_png(path, PREVIEW_CACHE)
        except (OSError, RuntimeError) as exc:
            self.status.configure(text=f"Initial preview failed: {exc}")
            return
        self.preview_path = PREVIEW_CACHE

    def _render_visible_preview(self, *, show_errors: bool) -> bool:
        document = self.layer_document
        if self.template_path is None or document is None:
            return False
        try:
            create_visible_court_preview_png(
                self.template_path,
                document,
                self.layer_visibility,
                PREVIEW_CACHE,
                color_overrides=self.layer_color_overrides,
                custom_floor_images=self._visible_custom_floor_images(),
            )
        except (OSError, RuntimeError, ValueError, struct.error) as exc:
            if show_errors:
                messagebox.showerror("Court Preview failed", str(exc))
            else:
                self.status.configure(text=f"Preview refresh failed: {exc}")
            return False
        self.preview_path = PREVIEW_CACHE
        return True

    def _visible_custom_floor_images(self) -> list[dict]:
        images = []
        for layer_id, image in self.custom_floor_images.items():
            if not self.layer_visibility.get(layer_id, False):
                continue
            resolved = dict(image)
            resolved["path"] = str(self._resolve_custom_floor_path(str(image.get("path", ""))))
            images.append(resolved)
        return images

    def _start_floor_preview_warmup(self) -> None:
        document = self.layer_document
        template_path = self.template_path
        if document is None or template_path is None:
            return
        if self._warmed_template_path == template_path:
            return
        layer_ids = self._court_floor_layer_ids()
        layer_ids.update(
            layer.id
            for layer in document.layers
            if layer.kind != "group" and self.layer_visibility.get(layer.id, layer.visible)
        )
        if not layer_ids:
            return
        self._warmed_template_path = template_path

        def warm() -> None:
            try:
                warm_visible_preview_layers(template_path, document, layer_ids)
            except (OSError, RuntimeError, ValueError, struct.error):
                return

        threading.Thread(target=warm, daemon=True).start()

    def _court_floor_layer_ids(self) -> set[str]:
        document = self.layer_document
        if document is None:
            return set()
        floor_groups = [
            layer
            for layer in document.layers
            if layer.kind == "group" and self._court_floor_group_for(layer) == layer
        ]
        floor_ids: set[str] = set()
        for group in floor_groups:
            for layer in document.layers:
                if layer.parent_id == group.id:
                    floor_ids.add(layer.id)
                    floor_ids.update(self._descendant_layer_ids(layer.id))
        return floor_ids

    def _select_layer(self, layer_id: str | None) -> None:
        if layer_id and self.layers.exists(layer_id):
            self.layers.selection_set(layer_id)
            self.layers.focus(layer_id)
            self.layers.see(layer_id)
        else:
            self.layers.selection_remove(self.layers.selection())
        self._on_layer_select()

    def _show_preview(self) -> None:
        canvas = self.preview_canvas
        canvas.delete("all")
        if self.preview_path is None or not self.preview_path.exists():
            canvas.create_text(
                max(1, canvas.winfo_width() // 2),
                max(1, canvas.winfo_height() // 2),
                text="No court preview loaded.",
                fill="#d8dde8",
            )
            return
        try:
            from PIL import Image, ImageTk
        except ImportError:
            canvas.create_text(
                max(1, canvas.winfo_width() // 2),
                max(1, canvas.winfo_height() // 2),
                text="Pillow is required for the court preview.",
                fill="#d8dde8",
            )
            return

        canvas_width = max(1, canvas.winfo_width())
        canvas_height = max(1, canvas.winfo_height())
        with Image.open(self.preview_path) as opened:
            image = opened.convert("RGBA")
        scale = min(canvas_width / image.width, canvas_height / image.height)
        display_width = max(1, round(image.width * scale))
        display_height = max(1, round(image.height * scale))
        image = image.resize((display_width, display_height), Image.Resampling.LANCZOS)
        self.preview_image = ImageTk.PhotoImage(image)
        left = (canvas_width - display_width) // 2
        top = (canvas_height - display_height) // 2
        self.preview_rect = (left, top, display_width, display_height)
        canvas.create_image(left, top, image=self.preview_image, anchor=tk.NW)
        self._draw_selected_layer_bounds()

    def _draw_selected_layer_bounds(self) -> None:
        layer = self._selected_layer()
        document = self.layer_document
        if layer is None or document is None or self.preview_rect is None:
            return
        x, y, width, height = layer.bbox
        if width <= 0 or height <= 0 or document.width <= 0 or document.height <= 0:
            return
        left, top, preview_width, preview_height = self.preview_rect
        scale_x = preview_width / document.width
        scale_y = preview_height / document.height
        rect = (
            left + x * scale_x,
            top + y * scale_y,
            left + (x + width) * scale_x,
            top + (y + height) * scale_y,
        )
        self.preview_canvas.create_rectangle(rect, outline="#ffcc33", width=2)
        self.preview_canvas.create_text(
            rect[0] + 6,
            rect[1] + 6,
            text=layer.name,
            anchor=tk.NW,
            fill="#ffcc33",
        )

    def _bounds_label(self, bbox: tuple[int, int, int, int]) -> str:
        x, y, width, height = bbox
        if width <= 0 or height <= 0:
            return "-"
        return f"{x}, {y}, {width} x {height}"


def find_photoshop_executable() -> Path | None:
    roots = [
        Path(os.environ.get("ProgramFiles", r"C:\Program Files")),
        Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")),
    ]
    candidates: list[Path] = []
    for root in roots:
        adobe_dir = root / "Adobe"
        if not adobe_dir.exists():
            continue
        candidates.extend(adobe_dir.glob("Adobe Photoshop*\\Photoshop.exe"))
    return sorted(candidates, reverse=True)[0] if candidates else None


def main() -> None:
    app = CourtCreatorApp()
    app.mainloop()


if __name__ == "__main__":
    main()
