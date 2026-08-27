from __future__ import annotations

import hashlib
import json
import os
import sys
import threading
from pathlib import Path

from dotenv import load_dotenv
from PySide6.QtCore import QObject, QSettings, Qt, QThread, Signal
from PySide6.QtGui import QColor
from PySide6.QtWidgets import (
    QApplication, QCheckBox, QComboBox, QFileDialog, QFormLayout, QHBoxLayout,
    QLabel, QLineEdit, QMainWindow, QMessageBox, QPushButton, QProgressBar,
    QSpinBox, QSplitter, QTableWidget, QTableWidgetItem, QTabWidget, QTextEdit,
    QVBoxLayout, QWidget
)

from excel_export import export_weekly_workbook
from extractor import ExtractionResult, ScreenshotExtraction, extract_many
from matcher import MemberMatcher, Observation, build_weekly_data, observations_from_extractions
from members import Member, MemberLoadResult, load_members_from_google_sheet, load_members_from_xlsx
from avatars import AvatarStore
from storage import AliasStore, ExtractionCache

load_dotenv()

APP_NAME = "Last War Weekly Extractor"


class DropLineEdit(QLineEdit):
    files_dropped = Signal(list)

    def __init__(self):
        super().__init__()
        self.setAcceptDrops(True)
        self.setPlaceholderText("Drop screenshot files/folders here, or use Add screenshots...")

    def dragEnterEvent(self, event):
        if event.mimeData().hasUrls():
            event.acceptProposedAction()

    def dropEvent(self, event):
        paths = [u.toLocalFile() for u in event.mimeData().urls() if u.toLocalFile()]
        if paths:
            self.files_dropped.emit(paths)
        event.acceptProposedAction()


PROMPT_CACHE_VERSION = "weekly-extractor-v3-avatar-bbox-2026-08-24"


class ExtractionWorker(QObject):
    progress = Signal(int, int, str)
    finished = Signal(object)
    failed = Signal(str)

    def __init__(self, paths, model, base_url, api_style, rpm, cache, use_cache=True):
        super().__init__()
        self.paths = paths
        self.model = model
        self.base_url = base_url
        self.api_style = api_style
        self.rpm = rpm
        self.cache = cache
        self.use_cache = use_cache
        self.cancel_event = threading.Event()

    def cancel(self):
        self.cancel_event.set()

    def _cache_key(self, path: Path) -> str:
        h = hashlib.sha256()
        h.update(path.read_bytes())
        h.update(self.model.encode("utf-8"))
        h.update(self.base_url.encode("utf-8"))
        h.update(self.api_style.encode("ascii"))
        h.update(PROMPT_CACHE_VERSION.encode("ascii"))
        return h.hexdigest()

    def run(self):
        try:
            cached: dict[Path, ExtractionResult] = {}
            uncached: list[Path] = []
            for path in self.paths:
                key = self._cache_key(path)
                raw = self.cache.get(key) if self.use_cache else None
                if raw:
                    try:
                        extraction = ScreenshotExtraction.model_validate_json(raw)
                        cached[path] = ExtractionResult(path, extraction, None)
                    except Exception:
                        uncached.append(path)
                else:
                    uncached.append(path)

            completed = 0
            for path in self.paths:
                if path in cached:
                    completed += 1
                    result = cached[path]
                    self.progress.emit(
                        completed, len(self.paths),
                        f"{path.name} - cached {result.extraction.detected_day}: {len(result.extraction.rows)} rows"
                    )

            fresh_by_path: dict[Path, ExtractionResult] = {}
            def cb(done, total, result: ExtractionResult):
                nonlocal completed
                completed += 1
                status = result.error or (
                    f"{result.extraction.detected_day}: {len(result.extraction.rows)} rows"
                    if result.extraction else "unknown"
                )
                self.progress.emit(completed, len(self.paths), f"{result.image_path.name} - {status}")

            if uncached:
                fresh = extract_many(
                    uncached,
                    model=self.model,
                    base_url=self.base_url,
                    api_style=self.api_style,
                    requests_per_minute=self.rpm,
                    progress=cb,
                    cancel_event=self.cancel_event,
                )
                for result in fresh:
                    fresh_by_path[result.image_path] = result
                    if result.extraction and not result.error:
                        self.cache.put(
                            self._cache_key(result.image_path), result.image_path.name,
                            result.extraction.model_dump_json()
                        )

            ordered = [cached.get(path) or fresh_by_path.get(path) for path in self.paths]
            self.finished.emit([r for r in ordered if r is not None])
        except Exception as exc:
            self.failed.emit(str(exc))


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle(APP_NAME)
        self.resize(1280, 820)
        self.settings = QSettings("LastWarTools", "WeeklyExtractor")
        app_dir = Path.home() / ".lastwar_weekly_extractor"
        self.alias_store = AliasStore(app_dir / "app.sqlite3")
        self.avatar_store = AvatarStore(app_dir / "app.sqlite3")
        self.extraction_cache = ExtractionCache(app_dir / "app.sqlite3")

        self.members: list[Member] = []
        self.member_source = ""
        self.member_warnings: list[str] = []
        self.screenshot_paths: list[Path] = []
        self.extraction_results: list[ExtractionResult] = []
        self.observations: list[Observation] = []
        self.base_issues: list[str] = []
        self.matcher: MemberMatcher | None = None
        self.worker_thread: QThread | None = None
        self.worker: ExtractionWorker | None = None

        self.tabs = QTabWidget()
        self.setCentralWidget(self.tabs)
        self._build_setup_tab()
        self._build_import_tab()
        self._build_review_tab()
        self._build_export_tab()
        self._restore_settings()

    def _build_setup_tab(self):
        tab = QWidget()
        layout = QVBoxLayout(tab)
        form = QFormLayout()

        self.source_type = QComboBox()
        self.source_type.addItems(["Local Excel workbook", "Google Sheet URL"])
        self.source_value = QLineEdit()
        self.source_value.setPlaceholderText("Path to workbook or Google Sheet URL")
        browse = QPushButton("Browse...")
        browse.clicked.connect(self._browse_members)
        source_row = QHBoxLayout()
        source_row.addWidget(self.source_value, 1)
        source_row.addWidget(browse)
        form.addRow("Member source", self.source_type)
        form.addRow("Source", source_row)

        self.sheet_name = QLineEdit("Members")
        form.addRow("Worksheet", self.sheet_name)

        self.model_edit = QLineEdit(os.getenv("OPENAI_MODEL", "gpt-5.6-terra"))
        self.base_url_edit = QLineEdit(os.getenv("OPENAI_BASE_URL", "https://api.openai.com/v1"))
        self.api_style = QComboBox()
        self.api_style.addItem("Responses API (OpenAI)", "responses")
        self.api_style.addItem("Chat Completions API (local)", "chat")
        self.api_style.setCurrentIndex(1 if os.getenv("OPENAI_API_STYLE", "responses") == "chat" else 0)
        self.rpm_spin = QSpinBox()
        self.rpm_spin.setRange(1, 30)
        self.rpm_spin.setValue(28)
        self.rpm_spin.setToolTip("28 RPM is the default safety margin under your 30 RPM limit.")
        form.addRow("Vision model", self.model_edit)
        form.addRow("API base URL", self.base_url_edit)
        form.addRow("API style", self.api_style)
        form.addRow("Requests / minute", self.rpm_spin)

        layout.addLayout(form)
        buttons = QHBoxLayout()
        load_btn = QPushButton("Load members")
        load_btn.clicked.connect(self._load_members)
        buttons.addWidget(load_btn)
        buttons.addStretch(1)
        layout.addLayout(buttons)

        self.members_status = QTextEdit()
        self.members_status.setReadOnly(True)
        layout.addWidget(self.members_status, 1)
        self.tabs.addTab(tab, "1. Setup")

    def _build_import_tab(self):
        tab = QWidget()
        layout = QVBoxLayout(tab)
        self.drop_line = DropLineEdit()
        self.drop_line.files_dropped.connect(self._add_paths)
        layout.addWidget(self.drop_line)

        row = QHBoxLayout()
        add_btn = QPushButton("Add screenshots...")
        add_btn.clicked.connect(self._choose_screenshots)
        folder_btn = QPushButton("Add folder...")
        folder_btn.clicked.connect(self._choose_folder)
        clear_btn = QPushButton("Clear")
        clear_btn.clicked.connect(self._clear_screenshots)
        self.extract_btn = QPushButton("Extract all")
        self.extract_btn.clicked.connect(self._start_extraction)
        self.cancel_btn = QPushButton("Cancel")
        self.cancel_btn.clicked.connect(self._cancel_extraction)
        self.cancel_btn.setEnabled(False)
        self.use_cache = QCheckBox("Reuse cached extractions")
        self.use_cache.setChecked(True)
        self.use_cache.setToolTip("Identical screenshots already extracted with this model/prompt are not sent again.")
        for w in (add_btn, folder_btn, clear_btn, self.extract_btn, self.cancel_btn):
            row.addWidget(w)
        row.addWidget(self.use_cache)
        row.addStretch(1)
        layout.addLayout(row)

        self.import_table = QTableWidget(0, 2)
        self.import_table.setHorizontalHeaderLabels(["Screenshot", "Status"])
        self.import_table.horizontalHeader().setStretchLastSection(True)
        layout.addWidget(self.import_table, 1)
        self.progress = QProgressBar()
        layout.addWidget(self.progress)
        self.tabs.addTab(tab, "2. Import")

    def _build_review_tab(self):
        tab = QWidget()
        layout = QVBoxLayout(tab)
        self.review_table = QTableWidget(0, 10)
        self.review_table.setHorizontalHeaderLabels([
            "Day", "Rank", "Raw name", "Visible ID", "Points", "Matched ID",
            "Matched member", "Method", "Confidence", "Issue"
        ])
        self.review_table.setSelectionBehavior(QTableWidget.SelectRows)
        self.review_table.setEditTriggers(QTableWidget.NoEditTriggers)
        self.review_table.itemSelectionChanged.connect(self._review_selection_changed)
        self.review_table.horizontalHeader().setStretchLastSection(True)
        layout.addWidget(self.review_table, 1)

        resolve_row = QHBoxLayout()
        resolve_row.addWidget(QLabel("Resolve selected as:"))
        self.member_combo = QComboBox()
        self.member_combo.setMinimumWidth(400)
        resolve_row.addWidget(self.member_combo, 1)
        self.remember_alias = QCheckBox("Remember name as alias")
        self.remember_alias.setChecked(True)
        resolve_row.addWidget(self.remember_alias)
        resolve_btn = QPushButton("Assign")
        resolve_btn.clicked.connect(self._assign_selected)
        resolve_row.addWidget(resolve_btn)
        layout.addLayout(resolve_row)

        self.review_summary = QLabel("No extraction results yet.")
        layout.addWidget(self.review_summary)
        self.tabs.addTab(tab, "3. Review")

    def _build_export_tab(self):
        tab = QWidget()
        layout = QVBoxLayout(tab)
        self.export_summary = QTextEdit()
        self.export_summary.setReadOnly(True)
        layout.addWidget(self.export_summary, 1)
        export_btn = QPushButton("Export weekly Excel...")
        export_btn.clicked.connect(self._export_excel)
        layout.addWidget(export_btn)
        self.tabs.addTab(tab, "4. Export")

    def _restore_settings(self):
        self.source_type.setCurrentIndex(int(self.settings.value("source_type", 0)))
        self.source_value.setText(str(self.settings.value("source_value", "")))
        self.sheet_name.setText(str(self.settings.value("sheet_name", "Members")))
        self.model_edit.setText(str(self.settings.value("model", self.model_edit.text())))
        self.base_url_edit.setText(str(self.settings.value("base_url", self.base_url_edit.text())))
        saved_style = str(self.settings.value("api_style", self.api_style.currentData()))
        self.api_style.setCurrentIndex(max(0, self.api_style.findData(saved_style)))
        self.rpm_spin.setValue(int(self.settings.value("rpm", 28)))

    def closeEvent(self, event):
        self.settings.setValue("source_type", self.source_type.currentIndex())
        self.settings.setValue("source_value", self.source_value.text())
        self.settings.setValue("sheet_name", self.sheet_name.text())
        self.settings.setValue("model", self.model_edit.text())
        self.settings.setValue("base_url", self.base_url_edit.text())
        self.settings.setValue("api_style", self.api_style.currentData())
        self.settings.setValue("rpm", self.rpm_spin.value())
        super().closeEvent(event)

    def _browse_members(self):
        path, _ = QFileDialog.getOpenFileName(self, "Select members workbook", "", "Excel (*.xlsx)")
        if path:
            self.source_type.setCurrentIndex(0)
            self.source_value.setText(path)

    def _load_members(self):
        try:
            if self.source_type.currentIndex() == 0:
                path = Path(self.source_value.text().strip())
                result = load_members_from_xlsx(path, self.sheet_name.text().strip() or "Members")
            else:
                result = load_members_from_google_sheet(
                    self.source_value.text().strip(), self.sheet_name.text().strip() or "Members"
                )
            self._apply_member_result(result)
        except Exception as exc:
            QMessageBox.critical(self, "Could not load members", str(exc))

    def _apply_member_result(self, result: MemberLoadResult):
        self.members = result.members
        self.member_source = result.source_description
        self.member_warnings = result.warnings
        self.matcher = MemberMatcher(self.members, self.alias_store, self.avatar_store)
        self.member_combo.clear()
        for m in sorted(self.members, key=lambda x: x.member_id):
            self.member_combo.addItem(f"{m.member_id} - {m.name}", m.member_id)
        avatar_members, avatar_samples = self.avatar_store.stats()
        text = [
            f"Loaded {len(self.members)} active members from {result.source_description}.",
            f"Avatar reference library: {avatar_members} members / {avatar_samples} samples.",
            "Trusted ID/name/alias matches automatically teach the local avatar library.",
        ]
        if result.warnings:
            text.append("\nWarnings:")
            text.extend(f"- {w}" for w in result.warnings)
        self.members_status.setPlainText("\n".join(text))
        if self.extraction_results:
            self._rebuild_matches()

    def _choose_screenshots(self):
        paths, _ = QFileDialog.getOpenFileNames(
            self, "Select screenshots", "", "Images (*.png *.jpg *.jpeg *.webp *.gif)"
        )
        self._add_paths(paths)

    def _choose_folder(self):
        folder = QFileDialog.getExistingDirectory(self, "Select screenshot folder")
        if folder:
            self._add_paths([folder])

    def _add_paths(self, raw_paths):
        allowed = {".png", ".jpg", ".jpeg", ".webp", ".gif"}
        collected = list(self.screenshot_paths)
        for raw in raw_paths:
            p = Path(raw)
            if p.is_dir():
                collected.extend(sorted(c for c in p.iterdir() if c.is_file() and c.suffix.lower() in allowed))
            elif p.is_file() and p.suffix.lower() in allowed:
                collected.append(p)
        self.screenshot_paths = list(dict.fromkeys(p.resolve() for p in collected))
        self._refresh_import_table()

    def _clear_screenshots(self):
        self.screenshot_paths = []
        self.extraction_results = []
        self.observations = []
        self._refresh_import_table()
        self._refresh_review_table()

    def _refresh_import_table(self):
        self.import_table.setRowCount(len(self.screenshot_paths))
        result_by_path = {r.image_path.resolve(): r for r in self.extraction_results}
        for row, path in enumerate(self.screenshot_paths):
            self.import_table.setItem(row, 0, QTableWidgetItem(str(path)))
            result = result_by_path.get(path.resolve())
            status = "Pending"
            if result:
                if result.error:
                    status = f"Error: {result.error}"
                elif result.extraction:
                    status = f"{result.extraction.detected_day.title()} - {len(result.extraction.rows)} rows"
            self.import_table.setItem(row, 1, QTableWidgetItem(status))
        self.import_table.resizeColumnsToContents()

    def _start_extraction(self):
        if not self.members:
            QMessageBox.warning(self, "Members not loaded", "Load the Members worksheet first.")
            return
        if not self.screenshot_paths:
            QMessageBox.warning(self, "No screenshots", "Add screenshots first.")
            return
        local_endpoint = any(host in self.base_url_edit.text().casefold() for host in ("localhost", "127.0.0.1", "::1"))
        if not os.getenv("OPENAI_API_KEY") and not local_endpoint:
            QMessageBox.warning(
                self, "API key missing",
                "OPENAI_API_KEY is not set. Put it in the environment or a .env file."
            )
            return

        self.extract_btn.setEnabled(False)
        self.cancel_btn.setEnabled(True)
        self.progress.setRange(0, len(self.screenshot_paths))
        self.progress.setValue(0)
        self.extraction_results = []

        self.worker_thread = QThread()
        self.worker = ExtractionWorker(
            list(self.screenshot_paths), self.model_edit.text().strip(),
            self.base_url_edit.text().strip(), self.api_style.currentData(), self.rpm_spin.value(),
            self.extraction_cache, self.use_cache.isChecked()
        )
        self.worker.moveToThread(self.worker_thread)
        self.worker_thread.started.connect(self.worker.run)
        self.worker.progress.connect(self._on_extract_progress)
        self.worker.finished.connect(self._on_extract_finished)
        self.worker.failed.connect(self._on_extract_failed)
        self.worker.finished.connect(self.worker_thread.quit)
        self.worker.failed.connect(self.worker_thread.quit)
        self.worker_thread.finished.connect(self.worker_thread.deleteLater)
        self.worker_thread.start()

    def _cancel_extraction(self):
        if self.worker:
            self.worker.cancel()
            self.cancel_btn.setEnabled(False)

    def _on_extract_progress(self, done, total, text):
        self.progress.setValue(done)
        self.statusBar().showMessage(text)

    def _on_extract_finished(self, results):
        self.extraction_results = results
        self.extract_btn.setEnabled(True)
        self.cancel_btn.setEnabled(False)
        self.worker = None
        self._refresh_import_table()
        self._rebuild_matches()
        self.tabs.setCurrentIndex(2)

    def _on_extract_failed(self, error):
        self.extract_btn.setEnabled(True)
        self.cancel_btn.setEnabled(False)
        self.worker = None
        QMessageBox.critical(self, "Extraction failed", error)

    def _rebuild_matches(self):
        if not self.matcher:
            return
        self.observations, self.base_issues = observations_from_extractions(self.extraction_results)

        # Two-pass matching is deliberate. First establish trusted identities from
        # visible IDs, exact names and saved aliases across the ENTIRE batch. Those
        # observations teach the persistent avatar library. Only then use avatars
        # for unresolved rows, so screenshot ordering does not affect the result.
        for obs in self.observations:
            self.matcher.match_deterministic(obs)
        for obs in self.observations:
            if obs.matched_member_id is not None:
                self.matcher.learn_avatar(obs)
        for obs in self.observations:
            if obs.matched_member_id is None:
                self.matcher.match_avatar(obs)

        self._refresh_review_table()
        self._refresh_export_summary()

    def _refresh_review_table(self):
        self.review_table.setRowCount(len(self.observations))
        for r, obs in enumerate(self.observations):
            values = [
                obs.day.title(), obs.rank, obs.raw_name, obs.raw_player_id, f"{obs.points:,}",
                obs.matched_member_id, obs.matched_member_name, obs.match_method,
                f"{obs.match_confidence:.0%}", obs.issue or ""
            ]
            for c, value in enumerate(values):
                item = QTableWidgetItem("" if value is None else str(value))
                item.setData(Qt.UserRole, r)
                if obs.issue:
                    item.setBackground(QColor("#FCE4D6"))
                self.review_table.setItem(r, c, item)
        self.review_table.resizeColumnsToContents()
        unmatched = sum(1 for o in self.observations if o.matched_member_id is None)
        avatar_auto = sum(1 for o in self.observations if o.match_method == "avatar_auto")
        avatar_members, avatar_samples = self.avatar_store.stats()
        self.review_summary.setText(
            f"{len(self.observations)} observations; {unmatched} unmatched; "
            f"{avatar_auto} avatar auto-matched. Avatar library: "
            f"{avatar_members} members / {avatar_samples} samples. "
            "Low-confidence avatar/fuzzy candidates remain suggestions only."
        )

    def _review_selection_changed(self):
        rows = sorted({i.row() for i in self.review_table.selectedIndexes()})
        if len(rows) != 1 or not self.members:
            return
        obs = self.observations[rows[0]]
        if obs.matched_member_id is not None:
            idx = self.member_combo.findData(obs.matched_member_id)
            if idx >= 0:
                self.member_combo.setCurrentIndex(idx)
        elif obs.alternatives:
            idx = self.member_combo.findData(obs.alternatives[0][0])
            if idx >= 0:
                self.member_combo.setCurrentIndex(idx)
            self.statusBar().showMessage(
                "Suggestions: " + "; ".join(
                    f"{mid} {name} ({score:.0%})" for mid, name, score in obs.alternatives
                )
            )

    def _assign_selected(self):
        if not self.matcher:
            return
        rows = sorted({i.row() for i in self.review_table.selectedIndexes()})
        if len(rows) != 1:
            QMessageBox.information(self, "Select one row", "Select exactly one observation to resolve.")
            return
        member_id = self.member_combo.currentData()
        if member_id is None:
            return
        self.matcher.manual_assign(
            self.observations[rows[0]], int(member_id), remember_alias=self.remember_alias.isChecked()
        )
        self._refresh_review_table()
        self.review_table.selectRow(rows[0])
        self._refresh_export_summary()

    def _refresh_export_summary(self):
        if not self.members:
            self.export_summary.setPlainText("Load members first.")
            return
        weekly = build_weekly_data(self.observations, self.members, self.base_issues + self.member_warnings)
        avatar_members, avatar_samples = self.avatar_store.stats()
        failed_files = [r.image_path.name for r in self.extraction_results if r.error]
        lines = [
            f"Members: {len(self.members)}",
            f"Screenshots: {len(self.screenshot_paths)}",
            f"Extracted observations: {len(self.observations)}",
            f"Avatar auto-matches: {sum(1 for o in self.observations if o.match_method == 'avatar_auto')}",
            f"Avatar reference library: {avatar_members} members / {avatar_samples} samples",
            f"Unmatched observations: {sum(1 for o in self.observations if o.matched_member_id is None)}",
            f"Issues/warnings: {len(weekly.issues)}",
            "",
            "Missing active members by detected day:",
        ]
        if weekly.missing_by_day:
            for day, missing in weekly.missing_by_day.items():
                lines.append(f"- {day.title()}: {len(missing)} missing")
        else:
            lines.append("- No days have been extracted yet.")
        if failed_files:
            lines.extend(["", "Screenshots requiring manual processing:"])
            for name in failed_files:
                lines.append(f"- {name}")
        self.export_summary.setPlainText("\n".join(lines))

    def _export_excel(self):
        if not self.members or not self.observations:
            QMessageBox.warning(self, "Nothing to export", "Load members and extract screenshots first.")
            return
        path, _ = QFileDialog.getSaveFileName(
            self, "Export weekly Excel", "weekly_scores.xlsx", "Excel (*.xlsx)"
        )
        if not path:
            return
        if not path.lower().endswith(".xlsx"):
            path += ".xlsx"
        weekly = build_weekly_data(self.observations, self.members, self.base_issues + self.member_warnings)
        try:
            export_weekly_workbook(Path(path), self.members, weekly, self.alias_store, self.member_source)
        except Exception as exc:
            QMessageBox.critical(self, "Export failed", str(exc))
            return
        QMessageBox.information(self, "Export complete", f"Saved:\n{path}")


def main():
    app = QApplication(sys.argv)
    app.setApplicationName(APP_NAME)
    window = MainWindow()
    window.show()
    raise SystemExit(app.exec())


if __name__ == "__main__":
    main()
