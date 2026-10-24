package com.idas3.android;

import android.app.Activity;
import android.app.Fragment;
import android.content.ClipData;
import android.content.ContentResolver;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.Bundle;
import android.provider.OpenableColumns;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.ArrayList;
import java.util.List;

// Android document picker for the Unity host. Unity's activity does not
// forward activity results, so a headless framework Fragment receives the
// picker result. Chosen documents are copied into an app-owned directory on a
// worker thread; C# polls the static state, progress and result paths.
@SuppressWarnings("deprecation")
public final class Idas3FilePicker {
    public static final int IDLE = 0, PICKING = 1, COPYING = 2, DONE = 3, CANCELLED = 4, FAILED = 5;
    private static final int REQUEST = 0x1D3;
    private static final String TAG = "idas3-file-picker";
    private static volatile int state = IDLE;
    private static volatile long copiedBytes, totalBytes;
    private static volatile String error = "", results = "";

    private Idas3FilePicker() { }

    public static int getState() { return state; }
    public static long getCopiedBytes() { return copiedBytes; }
    public static long getTotalBytes() { return totalBytes; }
    public static String getError() { return error; }
    // Copied absolute paths, newline separated, in picker order.
    public static String getResults() { return results; }
    public static void reset() { if (state != PICKING && state != COPYING) state = IDLE; }

    // mimeTypes: ';'-separated, or empty for any file. destination: an
    // app-owned directory. Returns false if a pick is already running.
    public static synchronized boolean pick(final Activity activity, String mimeTypes, String destination, boolean multiple) {
        if (state == PICKING || state == COPYING) return false;
        state = PICKING; copiedBytes = 0; totalBytes = 0; error = ""; results = "";
        final Bundle arguments = new Bundle();
        arguments.putString("mimeTypes", mimeTypes == null ? "" : mimeTypes);
        arguments.putString("destination", destination);
        arguments.putBoolean("multiple", multiple);
        activity.runOnUiThread(new Runnable() {
            @Override public void run() {
                try {
                    PickerFragment fragment = new PickerFragment();
                    fragment.setArguments(arguments);
                    activity.getFragmentManager().beginTransaction().add(fragment, TAG).commitAllowingStateLoss();
                } catch (Exception e) { fail(e); }
            }
        });
        return true;
    }

    private static void fail(Exception e) {
        error = e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage();
        state = FAILED;
    }

    public static final class PickerFragment extends Fragment {
        @Override public void onCreate(Bundle saved) {
            super.onCreate(saved);
            // A recreated activity has lost the original request.
            if (saved != null) { state = CANCELLED; remove(); return; }
            Bundle arguments = getArguments();
            Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT);
            intent.addCategory(Intent.CATEGORY_OPENABLE);
            intent.setType("*/*");
            String mimeTypes = arguments.getString("mimeTypes", "");
            if (!mimeTypes.isEmpty()) intent.putExtra(Intent.EXTRA_MIME_TYPES, mimeTypes.split(";"));
            intent.putExtra(Intent.EXTRA_ALLOW_MULTIPLE, arguments.getBoolean("multiple", false));
            try { startActivityForResult(intent, REQUEST); }
            catch (Exception e) { fail(e); remove(); }
        }

        @Override public void onActivityResult(int requestCode, int resultCode, Intent data) {
            if (requestCode != REQUEST) return;
            final String destination = getArguments().getString("destination");
            final ContentResolver resolver = getActivity().getContentResolver();
            remove();
            final List<Uri> uris = new ArrayList<>();
            if (resultCode == Activity.RESULT_OK && data != null) {
                ClipData clip = data.getClipData();
                if (clip != null) for (int i = 0; i < clip.getItemCount(); ++i) uris.add(clip.getItemAt(i).getUri());
                else if (data.getData() != null) uris.add(data.getData());
            }
            if (uris.isEmpty()) { state = CANCELLED; return; }
            state = COPYING;
            new Thread(new Runnable() {
                @Override public void run() {
                    try { copyAll(resolver, uris, new File(destination)); state = DONE; }
                    catch (Exception e) { fail(e); }
                }
            }, "idas3-file-copy").start();
        }

        private void remove() {
            try { getFragmentManager().beginTransaction().remove(this).commitAllowingStateLoss(); }
            catch (Exception ignored) { }
        }
    }

    private static void copyAll(ContentResolver resolver, List<Uri> uris, File destination) throws IOException {
        if (!destination.isDirectory() && !destination.mkdirs()) throw new IOException("Cannot create " + destination);
        List<String> names = new ArrayList<>();
        long total = 0;
        for (Uri uri : uris) {
            String name = "import.bin"; long size = -1;
            try (Cursor cursor = resolver.query(uri, new String[] { OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE }, null, null, null)) {
                if (cursor != null && cursor.moveToFirst()) {
                    if (!cursor.isNull(0)) name = cursor.getString(0);
                    if (!cursor.isNull(1)) size = cursor.getLong(1);
                }
            }
            // Keep only a plain file name inside the destination directory.
            name = new File(name.replace('\\', '/')).getName();
            if (name.isEmpty() || name.equals(".") || name.equals("..")) name = "import.bin";
            names.add(name);
            if (size > 0) total += size;
        }
        totalBytes = total;
        StringBuilder copied = new StringBuilder();
        byte[] buffer = new byte[1 << 20];
        for (int i = 0; i < uris.size(); ++i) {
            File target = new File(destination, names.get(i)), partial = new File(destination, names.get(i) + ".part");
            try (InputStream in = resolver.openInputStream(uris.get(i)); OutputStream out = new FileOutputStream(partial)) {
                if (in == null) throw new IOException("Cannot open " + names.get(i));
                int read;
                while ((read = in.read(buffer)) > 0) { out.write(buffer, 0, read); copiedBytes += read; }
            } catch (IOException e) { partial.delete(); throw e; }
            if (target.exists() && !target.delete()) { partial.delete(); throw new IOException("Cannot replace " + target.getName()); }
            if (!partial.renameTo(target)) { partial.delete(); throw new IOException("Cannot finish " + target.getName()); }
            if (copied.length() > 0) copied.append('\n');
            copied.append(target.getAbsolutePath());
        }
        results = copied.toString();
    }
}
