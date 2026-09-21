package com.voidstrap.android;

import android.content.Context;
import android.text.method.LinkMovementMethod;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.recyclerview.widget.LinearLayoutManager;
import androidx.recyclerview.widget.RecyclerView;

import com.google.android.material.button.MaterialButton;
import com.google.android.material.button.MaterialButtonToggleGroup;
import com.google.android.material.imageview.ShapeableImageView;
import com.google.android.material.textfield.TextInputEditText;

import java.io.IOException;
import java.text.DateFormat;
import java.text.NumberFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.Future;

final class ModsPacksTab {
    private static final int TYPE_CARD = 0;
    private static final int TYPE_MORE = 1;

    private final ModsFragment host;
    private final Context c;
    private final TextView status;
    private final LinearLayout filters;
    private final Adapter adapter = new Adapter();
    private final List<ModCatalog.Entry> entries = new ArrayList<>();
    private final Set<Long> seen = new HashSet<>();
    private boolean marketplace;
    private String sort = ModCatalog.GB_SORTS[0];
    private String query = "";
    private ModCatalog.Category category;
    private List<ModCatalog.Category> categories;
    private int page;
    private boolean hasMore;
    private boolean loading;
    private boolean loaded;
    private int generation;
    private Future<?> task;
    private final Runnable search;

    ModsPacksTab(ModsFragment host, View root) {
        this.host = host;
        this.c = root.getContext();
        RecyclerView list = (RecyclerView) root;
        list.setLayoutManager(new LinearLayoutManager(c));
        View v = LayoutInflater.from(c).inflate(R.layout.view_mods_packs_header, list, false);
        status = v.findViewById(R.id.packs_status);
        filters = v.findViewById(R.id.packs_filters);
        list.setAdapter(new androidx.recyclerview.widget.ConcatAdapter(new HeaderAdapter(v), adapter));
        MaterialButtonToggleGroup source = v.findViewById(R.id.packs_source);
        source.check(R.id.packs_gamebanana);
        source.addOnButtonCheckedListener((g, id, checked) -> {
            if (!checked) return;
            boolean m = id == R.id.packs_marketplace;
            if (m == marketplace) return;
            marketplace = m;
            sort = marketplace ? ModCatalog.MARKET_SORTS[0] : ModCatalog.GB_SORTS[0];
            category = null;
            buildFilters();
            load(true);
        });
        TextInputEditText box = v.findViewById(R.id.packs_search);
        search = () -> {
            String q = box.getText() == null ? "" : box.getText().toString().trim();
            if (q.equals(query)) return;
            query = q;
            if (!marketplace && !q.isEmpty() && q.length() < ModCatalog.MIN_SEARCH) return;
            load(true);
        };
        box.addTextChangedListener(new LibraryFragment.Watcher(() -> {
            box.removeCallbacks(search);
            box.postDelayed(search, 450);
        }));
        buildFilters();
    }

    void refresh() {
        if (!loaded && !loading) load(true);
    }

    void destroy() {
        if (task != null) task.cancel(true);
    }

    private void buildFilters() {
        filters.removeAllViews();
        String[] sorts = marketplace ? ModCatalog.MARKET_SORTS : ModCatalog.GB_SORTS;
        String[] sortLabels = c.getResources().getStringArray(marketplace ? R.array.mods_packs_market_sorts : R.array.mods_packs_sorts);
        int sel = 0;
        for (int i = 0; i < sorts.length; i++) if (sorts[i].equals(sort)) sel = i;
        SettingRows.choice(filters, c.getString(R.string.mods_packs_sort), null, sortLabels, sel, i -> {
            sort = sorts[i];
            load(true);
        });
        if (marketplace) return;
        List<String> labels = new ArrayList<>();
        labels.add(c.getString(R.string.mods_packs_all_categories));
        int csel = 0;
        if (categories != null) for (int i = 0; i < categories.size(); i++) {
            ModCatalog.Category cat = categories.get(i);
            labels.add(c.getString(R.string.mods_packs_category, cat.name, cat.count));
            if (category != null && category.id == cat.id) csel = i + 1;
        }
        SettingRows.choice(filters, c.getString(R.string.mods_packs_categories), categories == null ? c.getString(R.string.mods_packs_categories_loading) : null, labels.toArray(new String[0]), csel, i -> {
            category = i == 0 || categories == null ? null : categories.get(i - 1);
            load(true);
        });
        if (categories == null) loadCategories();
    }

    private void loadCategories() {
        host.store().work.execute(() -> {
            List<ModCatalog.Category> list;
            try {
                list = ModCatalog.categories();
            } catch (IOException | RuntimeException e) {
                return;
            }
            host.store().main.post(() -> {
                categories = list;
                if (host.isAdded() && !marketplace) buildFilters();
            });
        });
    }

    private void load(boolean reset) {
        if (task != null) task.cancel(true);
        int gen = ++generation;
        if (reset) {
            int old = adapter.getItemCount();
            entries.clear();
            seen.clear();
            page = 0;
            hasMore = false;
            adapter.notifyItemRangeRemoved(0, old);
        }
        loading = true;
        status.setText(c.getString(R.string.mods_packs_loading, marketplace ? ModCatalog.MARKETPLACE : ModCatalog.GAMEBANANA));
        int next = page + 1;
        boolean m = marketplace;
        String s = sort;
        String q = query;
        ModCatalog.Category cat = category;
        Context app = c.getApplicationContext();
        task = host.store().work.submit(() -> {
            ModCatalog.Page result = null;
            String error = null;
            try {
                result = m ? ModCatalog.marketplace(s, q) : ModCatalog.browse(app, next, s, cat, q);
            } catch (IOException | RuntimeException e) {
                error = app.getString(R.string.mods_packs_offline);
            }
            ModCatalog.Page r = result;
            String err = error;
            host.store().main.post(() -> {
                if (gen != generation || !host.isAdded()) return;
                loading = false;
                loaded = true;
                if (r == null) {
                    status.setText(err);
                    if (hasMore) adapter.notifyItemChanged(entries.size());
                    return;
                }
                page = next;
                int before = entries.size();
                boolean hadMore = hasMore;
                for (ModCatalog.Entry e : r.entries) if (seen.add(e.id)) entries.add(e);
                hasMore = !m && r.returned > 0 && (long) page * r.pageSize < r.total;
                if (hadMore) adapter.notifyItemRemoved(before);
                adapter.notifyItemRangeInserted(before, adapter.getItemCount() - before);
                status.setText(entries.isEmpty() ? c.getString(R.string.mods_packs_none)
                        : c.getResources().getQuantityString(R.plurals.mods_packs_shown, entries.size(), entries.size()));
                if (entries.size() == before && hasMore && r.returned > 0) load(false);
            });
        });
    }

    private String meta(ModCatalog.Entry e) {
        StringBuilder sb = new StringBuilder();
        if (!e.author.isEmpty()) sb.append(e.author);
        String cat = e.category.isEmpty() ? e.superCategory : e.category;
        if (!cat.isEmpty()) sb.append(sb.length() > 0 ? " · " : "").append(cat);
        NumberFormat nf = NumberFormat.getIntegerInstance();
        if (e.likes > 0) sb.append(" · ").append(c.getString(R.string.mods_packs_likes, nf.format(e.likes)));
        if (e.downloads > 0) sb.append(" · ").append(c.getString(R.string.mods_packs_downloads, nf.format(e.downloads)));
        if (!e.version.isEmpty()) sb.append(" · ").append(e.version);
        if (e.updated > 0) sb.append(" · ").append(Ui.ago(c, e.updated));
        return sb.toString();
    }

    private void open(ModCatalog.Entry e) {
        Context app = c.getApplicationContext();
        View content = LayoutInflater.from(c).inflate(R.layout.view_progress, null, false);
        ((TextView) content.findViewById(R.id.progress_text)).setText(R.string.mods_packs_opening);
        androidx.appcompat.app.AlertDialog progress = Ui.alert(c).setView(content).setCancelable(true).show();
        host.store().work.execute(() -> {
            ModCatalog.Entry d;
            String error = null;
            try {
                d = ModCatalog.detail(app, e);
                if (d == null) error = app.getString(R.string.mods_packs_hidden);
            } catch (IOException | RuntimeException ex) {
                d = null;
                error = app.getString(R.string.mods_packs_open_failed);
            }
            ModCatalog.Entry detail = d;
            String err = error;
            host.store().main.post(() -> {
                if (!progress.isShowing()) return;
                progress.dismiss();
                if (!host.isAdded()) return;
                if (detail == null) Ui.say(host.host(), err);
                else showDetail(detail);
            });
        });
    }

    private void showDetail(ModCatalog.Entry e) {
        View v = LayoutInflater.from(c).inflate(R.layout.view_mod_detail, new android.widget.FrameLayout(c), false);
        RecyclerView gallery = v.findViewById(R.id.detail_gallery);
        gallery.setLayoutManager(new LinearLayoutManager(c, LinearLayoutManager.HORIZONTAL, false));
        gallery.setAdapter(new Gallery(e.images));
        gallery.setVisibility(e.images.isEmpty() ? View.GONE : View.VISIBLE);
        String cat = e.category.isEmpty() ? e.superCategory : e.category;
        ((TextView) v.findViewById(R.id.detail_meta)).setText(c.getString(R.string.mods_packs_detail_meta, cat, e.author.isEmpty() ? e.source : e.author));
        NumberFormat nf = NumberFormat.getIntegerInstance();
        StringBuilder stats = new StringBuilder();
        if (e.downloads > 0) stats.append(c.getString(R.string.mods_packs_downloads, nf.format(e.downloads)));
        if (e.likes > 0) stats.append(stats.length() > 0 ? " · " : "").append(c.getString(R.string.mods_packs_likes, nf.format(e.likes)));
        if (e.views > 0) stats.append(stats.length() > 0 ? " · " : "").append(c.getString(R.string.mods_packs_views, nf.format(e.views)));
        if (e.updated > 0) stats.append(stats.length() > 0 ? " · " : "").append(c.getString(R.string.mods_packs_updated, DateFormat.getDateInstance(DateFormat.MEDIUM).format(new Date(e.updated))));
        TextView statsView = v.findViewById(R.id.detail_stats);
        statsView.setText(stats);
        statsView.setVisibility(stats.length() == 0 ? View.GONE : View.VISIBLE);
        View submitter = v.findViewById(R.id.detail_submitter);
        if (e.submitter != null && !e.submitter.name.isEmpty()) {
            ((TextView) v.findViewById(R.id.detail_author)).setText(e.submitter.name);
            String detail = e.submitter.title.isEmpty() ? "" : e.submitter.title;
            if (e.submitter.points > 0) detail += (detail.isEmpty() ? "" : " · ") + c.getString(R.string.mods_packs_points, nf.format(e.submitter.points));
            ((TextView) v.findViewById(R.id.detail_author_detail)).setText(detail);
            ShapeableImageView avatar = v.findViewById(R.id.detail_avatar);
            if (e.submitter.avatar.startsWith("https://")) Net.image(avatar, e.submitter.avatar, R.drawable.ic_people, Ui.dp(c, 40));
            else avatar.setImageResource(R.drawable.ic_people);
        } else {
            ((TextView) v.findViewById(R.id.detail_author)).setText(e.author.isEmpty() ? e.source : e.author);
            ((TextView) v.findViewById(R.id.detail_author_detail)).setText(e.source);
            ((ShapeableImageView) v.findViewById(R.id.detail_avatar)).setImageResource(R.drawable.ic_people);
        }
        v.findViewById(R.id.detail_page).setOnClickListener(x -> Ui.openWeb(c, e.profileUrl));
        submitter.setVisibility(View.VISIBLE);
        List<ModCatalog.ModFile> files = new ArrayList<>();
        for (ModCatalog.ModFile f : e.files) if (ModCatalog.inspectFile(f) == null) files.add(f);
        LinearLayout fileBox = v.findViewById(R.id.detail_files);
        TextView hint = v.findViewById(R.id.detail_hint);
        MaterialButton install = v.findViewById(R.id.detail_install);
        ModCatalog.ModFile[] chosen = {files.isEmpty() ? null : files.get(0)};
        Runnable updateHint = () -> {
            ModCatalog.ModFile f = chosen[0];
            if (f == null) {
                hint.setText(R.string.mods_packs_no_files);
                install.setEnabled(false);
                return;
            }
            String block = ModCatalog.androidBlock(e, f);
            StringBuilder h = new StringBuilder();
            if (f.size > 0) h.append(Ui.size(c, f.size));
            if (block != null) h.append(h.length() > 0 ? "\n" : "").append(block);
            else if (f.listingKnown && !f.listingRoblox && f.listingCache) h.append(h.length() > 0 ? "   " : "").append(c.getString(R.string.mods_packs_cache_hint));
            else h.append(h.length() > 0 ? "   " : "").append(c.getString(R.string.mods_packs_install_hint));
            if (!f.description.isEmpty()) h.append('\n').append(ModCatalog.plain(f.description));
            hint.setText(h);
            install.setEnabled(block == null);
        };
        if (files.size() > 1) {
            String[] names = new String[files.size()];
            for (int i = 0; i < names.length; i++) names[i] = files.get(i).name;
            SettingRows.choice(fileBox, c.getString(R.string.mods_packs_file), null, names, 0, i -> {
                chosen[0] = files.get(i);
                updateHint.run();
            });
        } else if (files.size() == 1) {
            TextView one = new TextView(c);
            one.setTextAppearance(R.style.TextAppearance_Voidstrap_Body);
            one.setText(files.get(0).name);
            one.setPadding(0, Ui.dp(c, 6), 0, 0);
            fileBox.addView(one);
        }
        updateHint.run();
        TextView desc = v.findViewById(R.id.detail_description);
        desc.setText(e.html.isEmpty() ? c.getString(R.string.mods_packs_no_description) : android.text.Html.fromHtml(e.html, android.text.Html.FROM_HTML_MODE_COMPACT));
        desc.setMovementMethod(LinkMovementMethod.getInstance());
        TextView license = v.findViewById(R.id.detail_license);
        StringBuilder lic = new StringBuilder(e.license);
        for (String[] rule : e.licenseRules) lic.append(lic.length() > 0 ? "\n" : "").append(rule[0]).append(": ").append(rule[1]);
        license.setText(lic);
        boolean hasLicense = lic.length() > 0;
        license.setVisibility(hasLicense ? View.VISIBLE : View.GONE);
        v.findViewById(R.id.detail_license_title).setVisibility(hasLicense ? View.VISIBLE : View.GONE);
        LinearLayout sections = v.findViewById(R.id.detail_sections);
        if (ModCatalog.GAMEBANANA.equals(e.source)) {
            String op = e.submitter == null ? "" : e.submitter.name;
            section(sections, c.getString(R.string.mods_packs_comments, e.commentCount), e.comments, e.commentCount, R.string.mods_packs_no_comments, e.profileUrl, op, true);
            section(sections, c.getString(R.string.mods_packs_updates, e.updateCount), e.updates, e.updateCount, R.string.mods_packs_no_updates, e.profileUrl, op, false);
            section(sections, c.getString(R.string.mods_packs_issues, e.issueCount), e.issues, e.issueCount, R.string.mods_packs_no_issues, e.profileUrl, op, false);
        }
        android.app.Dialog d = Ui.surface(host.host(), e.name, e.summary.length() > 160 ? e.summary.substring(0, 160) + "..." : e.summary, v, host::publishPresence);
        String byline = e.author.isEmpty() ? e.source : "by " + e.author;
        AppPresence.set(R.id.nav_mods, "Viewing " + e.name, e.summary.trim().isEmpty() ? byline : ModCatalog.plain(e.summary), e.iconUrl, e.name + (byline.isEmpty() ? "" : " " + byline), "View mod", e.profileUrl);
        install.setOnClickListener(x -> {
            ModCatalog.ModFile f = chosen[0];
            if (f == null) return;
            d.dismiss();
            host.runTask(R.string.mods_packs_installing, (app, cancel) -> {
                ManagedMods.Record r = ModCatalog.install(app, e, f, cancel, null);
                host.store().main.post(() -> {
                    if (host.isAdded()) host.selectTab(ModsFragment.TAB_LIBRARY);
                });
                return app.getString(R.string.mods_library_added, r.name);
            });
        });
        d.show();
    }

    private void section(LinearLayout parent, String title, List<ModCatalog.Post> posts, int total, int empty, String url, String op, boolean withReplies) {
        Row header = Row.inflate(parent);
        header.set(R.drawable.ic_megaphone, title, null);
        header.chevron.setVisibility(View.VISIBLE);
        LinearLayout body = new LinearLayout(c);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setPadding(Ui.dp(c, 8), 0, Ui.dp(c, 4), Ui.dp(c, 8));
        body.setVisibility(View.GONE);
        boolean[] fetched = {!withReplies};
        header.view.setOnClickListener(x -> {
            boolean open = body.getVisibility() != View.VISIBLE;
            body.setVisibility(open ? View.VISIBLE : View.GONE);
            header.chevron.setRotation(open ? 90 : 0);
            if (!open || fetched[0]) return;
            fetched[0] = true;
            TextView wait = caption(c.getString(R.string.mods_packs_replies_loading));
            wait.setPadding(0, Ui.dp(c, 8), 0, 0);
            body.addView(wait);
            host.store().work.execute(() -> {
                ModCatalog.loadReplies(posts);
                host.store().main.post(() -> fill(body, posts, total, empty, url, op));
            });
        });
        parent.addView(header.view);
        parent.addView(body);
        fill(body, posts, total, empty, url, op);
    }

    private void fill(LinearLayout body, List<ModCatalog.Post> posts, int total, int empty, String url, String op) {
        body.removeAllViews();
        for (ModCatalog.Post p : posts) post(body, p, 0, op, null);
        if (body.getChildCount() == 0 && (total <= 0 || posts.size() > 0)) body.addView(caption(c.getString(empty)));
        int shown = ModCatalog.count(posts);
        if (shown < total && url != null && url.startsWith("https://")) {
            TextView more = caption(posts.isEmpty() ? c.getString(R.string.mods_packs_posts_failed) : c.getString(R.string.mods_packs_posts_more, shown, total));
            more.setTextColor(Ui.attr(c, androidx.appcompat.R.attr.colorPrimary));
            more.setPadding(Ui.dp(c, 4), Ui.dp(c, 12), Ui.dp(c, 4), Ui.dp(c, 12));
            more.setBackgroundResource(R.drawable.vs_row);
            more.setOnClickListener(x -> Ui.openWeb(c, url));
            body.addView(more);
        }
    }

    private void post(LinearLayout parent, ModCatalog.Post p, int depth, String op, String replyingTo) {
        if (p.removed) return;
        LinearLayout item = new LinearLayout(c);
        item.setOrientation(LinearLayout.HORIZONTAL);
        item.setPadding(0, Ui.dp(c, 12), 0, Ui.dp(c, 4));
        int size = Ui.dp(c, depth == 0 ? 40 : 32);
        View avatar;
        if (p.avatar.isEmpty()) {
            TextView initial = new TextView(c);
            initial.setGravity(android.view.Gravity.CENTER);
            initial.setTextAppearance(R.style.TextAppearance_Voidstrap_RowTitle);
            initial.setTextColor(Ui.attr(c, androidx.appcompat.R.attr.colorPrimary));
            initial.setText(p.author.isEmpty() ? "?" : p.author.substring(0, p.author.offsetByCodePoints(0, 1)).toUpperCase(java.util.Locale.ROOT));
            android.graphics.drawable.GradientDrawable circle = new android.graphics.drawable.GradientDrawable();
            circle.setShape(android.graphics.drawable.GradientDrawable.OVAL);
            circle.setColor(androidx.core.graphics.ColorUtils.setAlphaComponent(Ui.attr(c, androidx.appcompat.R.attr.colorPrimary), 40));
            initial.setBackground(circle);
            avatar = initial;
        } else {
            ShapeableImageView image = new ShapeableImageView(c);
            image.setShapeAppearanceModel(new com.google.android.material.shape.ShapeAppearanceModel().withCornerSize(new com.google.android.material.shape.RelativeCornerSize(0.5f)));
            image.setScaleType(ImageView.ScaleType.CENTER_CROP);
            Net.image(image, p.avatar, R.color.vs_subtle, size);
            avatar = image;
        }
        avatar.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        if (!p.profile.isEmpty()) avatar.setOnClickListener(x -> Ui.openWeb(c, p.profile));
        item.addView(avatar, new LinearLayout.LayoutParams(size, size));

        LinearLayout col = new LinearLayout(c);
        col.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams cp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        cp.setMarginStart(Ui.dp(c, 12));
        item.addView(col, cp);

        LinearLayout meta = new LinearLayout(c);
        meta.setOrientation(LinearLayout.HORIZONTAL);
        meta.setGravity(android.view.Gravity.CENTER_VERTICAL);
        TextView name = new TextView(c);
        name.setTextAppearance(R.style.TextAppearance_Voidstrap_RowTitle);
        name.setTextSize(15);
        name.setText(p.author.isEmpty() ? c.getString(R.string.mods_packs_unknown_author) : p.author);
        name.setMaxLines(1);
        name.setEllipsize(android.text.TextUtils.TruncateAt.END);
        if (!p.profile.isEmpty()) name.setOnClickListener(x -> Ui.openWeb(c, p.profile));
        meta.addView(name, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, 0f));
        if (op != null && !op.isEmpty() && op.equals(p.author)) meta.addView(chip(c.getString(R.string.mods_packs_author_badge)));
        if (p.pinned) meta.addView(chip(c.getString(R.string.mods_packs_pinned_badge)));
        if (!p.status.isEmpty()) meta.addView(chip(p.status));
        if (p.time > 0) {
            TextView when = new TextView(c);
            when.setTextAppearance(R.style.TextAppearance_Voidstrap_Tertiary);
            when.setText(Ui.ago(c, p.time));
            when.setMaxLines(1);
            LinearLayout.LayoutParams wp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            wp.setMarginStart(Ui.dp(c, 8));
            meta.addView(when, wp);
        }
        col.addView(meta);

        String sub = replyingTo != null ? c.getString(R.string.mods_packs_replying_to, replyingTo) : p.authorTitle;
        if (!sub.isEmpty()) {
            TextView t = new TextView(c);
            t.setTextAppearance(R.style.TextAppearance_Voidstrap_Tertiary);
            t.setText(sub);
            t.setMaxLines(1);
            t.setEllipsize(android.text.TextUtils.TruncateAt.END);
            col.addView(t);
        }
        if (!p.title.isEmpty()) {
            TextView t = new TextView(c);
            t.setTextAppearance(R.style.TextAppearance_Voidstrap_RowTitle);
            t.setText(p.title);
            t.setPadding(0, Ui.dp(c, 6), 0, 0);
            col.addView(t);
        }
        for (String[] change : p.changes) {
            TextView t = new TextView(c);
            t.setTextAppearance(R.style.TextAppearance_Voidstrap_Body);
            android.text.SpannableStringBuilder line = new android.text.SpannableStringBuilder("\u2022  ");
            if (!change[0].isEmpty()) {
                int at = line.length();
                line.append(change[0]).append(": ");
                line.setSpan(new android.text.style.StyleSpan(android.graphics.Typeface.BOLD), at, line.length(), android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
            }
            line.append(change[1]);
            t.setText(line);
            t.setPadding(0, Ui.dp(c, 4), 0, 0);
            col.addView(t);
        }
        if (!p.body.isEmpty()) {
            TextView text = new TextView(c);
            text.setTextAppearance(R.style.TextAppearance_Voidstrap_Body);
            text.setText(p.body);
            text.setTextIsSelectable(true);
            text.setLineSpacing(Ui.dp(c, 2), 1f);
            text.setPadding(0, Ui.dp(c, 6), 0, 0);
            col.addView(text);
        }
        if (p.stamps > 0) {
            TextView stamps = new TextView(c);
            stamps.setTextAppearance(R.style.TextAppearance_Voidstrap_Tertiary);
            stamps.setText(NumberFormat.getIntegerInstance().format(p.stamps));
            android.graphics.drawable.Drawable like = androidx.core.content.ContextCompat.getDrawable(c, R.drawable.ic_thumb_like);
            if (like != null) {
                like = like.mutate();
                int s14 = Ui.dp(c, 14);
                like.setBounds(0, 0, s14, s14);
                like.setTint(Ui.attr(c, R.attr.vsTextTertiary));
                stamps.setCompoundDrawablesRelative(like, null, null, null);
                stamps.setCompoundDrawablePadding(Ui.dp(c, 6));
            }
            stamps.setPadding(0, Ui.dp(c, 6), 0, 0);
            col.addView(stamps);
        }
        parent.addView(item);

        boolean anyReply = false;
        for (ModCatalog.Post r : p.replies) anyReply |= !r.removed;
        if (!anyReply) return;
        if (depth >= 2) {
            for (ModCatalog.Post r : p.replies) post(parent, r, depth + 1, op, p.author);
            return;
        }
        LinearLayout thread = new LinearLayout(c);
        thread.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams tp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        tp.setMarginStart(size / 2 - Ui.dp(c, 1));
        View line = new View(c);
        line.setBackgroundColor(androidx.core.graphics.ColorUtils.setAlphaComponent(Ui.attr(c, R.attr.vsTextTertiary), 70));
        thread.addView(line, new LinearLayout.LayoutParams(Ui.dp(c, 2), ViewGroup.LayoutParams.MATCH_PARENT));
        LinearLayout replies = new LinearLayout(c);
        replies.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams rp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        rp.setMarginStart(Ui.dp(c, 12));
        thread.addView(replies, rp);
        for (ModCatalog.Post r : p.replies) post(replies, r, depth + 1, op, null);
        parent.addView(thread, tp);
    }

    private TextView caption(String text) {
        TextView t = new TextView(c);
        t.setTextAppearance(R.style.TextAppearance_Voidstrap_Caption);
        t.setText(text);
        return t;
    }

    private TextView chip(String text) {
        TextView t = new TextView(c);
        t.setTextAppearance(R.style.TextAppearance_Voidstrap_Tertiary);
        t.setTextSize(11);
        t.setTextColor(Ui.attr(c, androidx.appcompat.R.attr.colorPrimary));
        t.setText(text);
        t.setMaxLines(1);
        android.graphics.drawable.GradientDrawable bg = new android.graphics.drawable.GradientDrawable();
        bg.setCornerRadius(Ui.dp(c, 8));
        bg.setColor(androidx.core.graphics.ColorUtils.setAlphaComponent(Ui.attr(c, androidx.appcompat.R.attr.colorPrimary), 40));
        t.setBackground(bg);
        t.setPadding(Ui.dp(c, 8), Ui.dp(c, 2), Ui.dp(c, 8), Ui.dp(c, 2));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.setMarginStart(Ui.dp(c, 6));
        t.setLayoutParams(lp);
        return t;
    }

    private final class Gallery extends RecyclerView.Adapter<RecyclerView.ViewHolder> {
        private final List<ModCatalog.Image> images;

        Gallery(List<ModCatalog.Image> images) {
            this.images = images;
        }

        @NonNull
        @Override
        public RecyclerView.ViewHolder onCreateViewHolder(@NonNull ViewGroup parent, int viewType) {
            ShapeableImageView iv = new ShapeableImageView(c);
            RecyclerView.LayoutParams lp = new RecyclerView.LayoutParams(Ui.dp(c, 220), ViewGroup.LayoutParams.MATCH_PARENT);
            lp.setMarginEnd(Ui.dp(c, 8));
            iv.setLayoutParams(lp);
            iv.setScaleType(ImageView.ScaleType.CENTER_CROP);
            iv.setBackgroundResource(R.drawable.vs_icon_tile);
            iv.setShapeAppearanceModel(iv.getShapeAppearanceModel().withCornerSize(Ui.dp(c, 8)));
            return new RecyclerView.ViewHolder(iv) {
            };
        }

        @Override
        public void onBindViewHolder(@NonNull RecyclerView.ViewHolder h, int position) {
            ModCatalog.Image img = images.get(position);
            ShapeableImageView iv = (ShapeableImageView) h.itemView;
            iv.setContentDescription(img.caption == null || img.caption.isEmpty() ? c.getString(R.string.mods_packs_image) : img.caption);
            Net.image(iv, img.thumb, R.drawable.ic_image, Ui.dp(c, 220));
            iv.setOnClickListener(x -> Ui.openWeb(c, img.full));
        }

        @Override
        public int getItemCount() {
            return images.size();
        }
    }

    private final class Adapter extends RecyclerView.Adapter<RecyclerView.ViewHolder> {
        @Override
        public int getItemViewType(int position) {
            return position < entries.size() ? TYPE_CARD : TYPE_MORE;
        }

        @NonNull
        @Override
        public RecyclerView.ViewHolder onCreateViewHolder(@NonNull ViewGroup parent, int viewType) {
            if (viewType == TYPE_MORE) {
                MaterialButton more = new MaterialButton(parent.getContext(), null, androidx.appcompat.R.attr.borderlessButtonStyle);
                more.setText(R.string.mods_packs_more);
                more.setIconResource(R.drawable.ic_chevron_down);
                RecyclerView.LayoutParams lp = new RecyclerView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                more.setLayoutParams(lp);
                return new RecyclerView.ViewHolder(more) {
                };
            }
            return new RecyclerView.ViewHolder(LayoutInflater.from(parent.getContext()).inflate(R.layout.item_mod_card, parent, false)) {
            };
        }

        @Override
        public void onBindViewHolder(@NonNull RecyclerView.ViewHolder h, int position) {
            if (getItemViewType(position) == TYPE_MORE) {
                MaterialButton more = (MaterialButton) h.itemView;
                more.setEnabled(!loading);
                more.setOnClickListener(x -> load(false));
                return;
            }
            ModCatalog.Entry e = entries.get(position);
            View v = h.itemView;
            ((TextView) v.findViewById(R.id.card_title)).setText(e.name);
            TextView summary = v.findViewById(R.id.card_summary);
            summary.setText(e.summary);
            summary.setVisibility(e.summary.isEmpty() ? View.GONE : View.VISIBLE);
            ((TextView) v.findViewById(R.id.card_meta)).setText(meta(e));
            ShapeableImageView image = v.findViewById(R.id.card_image);
            if (e.iconUrl.startsWith("https://")) Net.image(image, e.iconUrl, R.drawable.ic_image, Ui.dp(c, 96));
            else image.setImageResource(R.drawable.ic_image);
            v.findViewById(R.id.card_page).setOnClickListener(x -> Ui.openWeb(c, e.profileUrl));
            v.findViewById(R.id.card_view).setOnClickListener(x -> open(e));
            v.setOnClickListener(x -> open(e));
        }

        @Override
        public int getItemCount() {
            return entries.size() + (hasMore ? 1 : 0);
        }
    }
}
