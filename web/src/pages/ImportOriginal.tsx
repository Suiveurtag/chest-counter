import { useRef, useState } from 'react';
import { ArchiveRestore, CheckCircle2, FileJson, FolderOpen } from 'lucide-react';
import { api, useLive } from '../api';
import { useLocale } from '../i18n';
import { useAction } from '../parts';

type Result = { imported: number; duplicates: number; skipped: number };

/** Brings the history of the original Chest Counter (its data.json) into this app. Used by Settings and the onboarding. */
export function ImportOriginal({ onImported }: { onImported?: (result: Result) => void }) {
  const { t } = useLocale();
  const [importResult, setImportResult] = useState<Result>();
  const [importFile, setImportFile] = useState<File>();
  const fileInput = useRef<HTMLInputElement>(null);
  const { data: original, error: originalError } = useLive<{ found: boolean; path: string }>('/api/import-original/default', []);
  const importAction = useAction();
  const importOriginal = () => importAction.run(async () => {
    setImportResult(undefined);
    const result = importFile
      ? await api.post<Result>('/api/import-original', JSON.parse(await importFile.text()))
      : await api.post<Result>('/api/import-original/default');
    setImportResult(result);
    onImported?.(result);
  });

  return (
    <section className="panel import-section" aria-labelledby="import-title">
      <div className="import-heading">
        <div className="import-symbol"><ArchiveRestore size={22} aria-hidden="true" /></div>
        <div><h2 id="import-title">{t('settings.importOriginal')}</h2><p className="note">{t('settings.importIntro')}</p></div>
      </div>
      <div className="import-source">
        <FileJson size={20} aria-hidden="true" />
        <div className="import-source-text">
          <strong>{importFile ? importFile.name : original?.found ? t('settings.importFound') : originalError ? t('settings.importUnavailable') : original ? t('settings.importMissing') : t('settings.importChecking')}</strong>
          <span className="selectable">{importFile ? t('settings.importChosen') : original?.found ? original.path : t('settings.importChooseHint')}</span>
        </div>
        {!importFile && original?.found && <CheckCircle2 className="import-check" size={19} aria-hidden="true" />}
      </div>
      <div className="actions import-actions">
        <button className="btn primary" disabled={importAction.busy || (!importFile && !original?.found)} onClick={importOriginal}><ArchiveRestore size={16} />{importAction.busy ? t('settings.importBusy') : t('settings.importButton')}</button>
        <button className="btn quiet" disabled={importAction.busy} onClick={() => fileInput.current?.click()}><FolderOpen size={16} />{t('settings.importChoose')}</button>
        {importFile && original?.found && <button className="btn quiet" disabled={importAction.busy} onClick={() => { setImportFile(undefined); setImportResult(undefined); importAction.clear(); }}>{t('settings.importUseDetected')}</button>}
        <input ref={fileInput} className="visually-hidden" type="file" tabIndex={-1} aria-hidden="true" accept=".json,application/json" onChange={(e) => { const file = e.target.files?.[0]; e.target.value = ''; if (file) { setImportFile(file); setImportResult(undefined); importAction.clear(); } }} />
      </div>
      <p className="note import-explanation">{t('settings.importOriginalHint')}</p>
      {importResult && <p className="import-result" role="status"><CheckCircle2 size={17} aria-hidden="true" />{t('settings.importOriginalResult', importResult)}</p>}
      {importAction.error && <p className="error" role="alert">{t('settings.importError', { message: importAction.error })}</p>}
    </section>
  );
}
