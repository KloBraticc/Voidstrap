package com.voidstrap.android;

import android.content.Context;
import android.content.res.Configuration;
import android.os.Bundle;
import android.text.format.DateUtils;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.Space;
import android.widget.TextView;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;

import com.google.android.material.button.MaterialButton;

import org.json.JSONException;

import java.io.IOException;
import java.text.DateFormat;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Calendar;
import java.util.Date;
import java.util.List;
import java.util.Locale;

public final class HomeFragment extends Page {
    private static final long FEED_REFRESH_MS = 10 * 60_000L;
    private static final long FEED_RETRY_MS = 2 * 60_000L;

    private View newsHeader;
    private TextView newsCount;
    private LinearLayout newsGrid;
    private TextView catalogStatus;
    private LinearLayout catalogGrid;
    private View continueEmpty;
    private LinearLayout continueGrid;

    private long newsDueAt;
    private long catalogDueAt;
    private String historySignature = "";
    private int gamesSeq;

    private final android.net.ConnectivityManager.NetworkCallback online = new android.net.ConnectivityManager.NetworkCallback() {
        @Override
        public void onAvailable(@NonNull android.net.Network network) {
            store.main.post(HomeFragment.this::reconnected);
        }

        @Override
        public void onBlockedStatusChanged(@NonNull android.net.Network network, boolean blocked) {
            if (!blocked) store.main.post(HomeFragment.this::reconnected);
        }
    };
    private android.net.ConnectivityManager watching;
    private long watchingSince;

    public HomeFragment() {
        super(R.layout.fragment_home);
    }

    @Override
    public void onStart() {
        super.onStart();
        android.net.ConnectivityManager cm = requireContext().getSystemService(android.net.ConnectivityManager.class);
        if (cm == null) return;
        watchingSince = android.os.SystemClock.elapsedRealtime();
        try {
            cm.registerDefaultNetworkCallback(online);
            watching = cm;
        } catch (RuntimeException ignored) {
        }
    }

    @Override
    public void onStop() {
        if (watching != null) {
            try {
                watching.unregisterNetworkCallback(online);
            } catch (RuntimeException ignored) {
            }
            watching = null;
        }
        super.onStop();
    }

    private void reconnected() {
        if (!isAdded() || getView() == null || isHidden()) return;
        if (android.os.SystemClock.elapsedRealtime() - watchingSince < 3000) return;
        if (newsGrid.getChildCount() > 0 && catalogGrid.getChildCount() > 0) return;
        newsDueAt = 0;
        catalogDueAt = 0;
        historySignature = "";
        refresh();
    }

    @Override
    public void onViewCreated(@NonNull View v, @Nullable Bundle saved) {
        newsHeader = v.findViewById(R.id.news_header);
        newsCount = v.findViewById(R.id.news_count);
        newsGrid = v.findViewById(R.id.news_grid);
        catalogStatus = v.findViewById(R.id.catalog_status);
        catalogGrid = v.findViewById(R.id.catalog_grid);
        continueEmpty = v.findViewById(R.id.continue_empty);
        continueGrid = v.findViewById(R.id.continue_grid);
        v.findViewById(R.id.news_explore).setOnClickListener(x -> exploreNews());
        newsDueAt = 0;
        catalogDueAt = 0;
        historySignature = "";
    }

    @Override
    protected void refresh() {
        long now = System.currentTimeMillis();
        if (now >= newsDueAt) loadNews();
        if (now >= catalogDueAt) loadCatalog();
        String signature = store.history.size() + ":" + (store.history.isEmpty() ? 0 : store.history.get(0).time);
        if (!signature.equals(historySignature)) {
            historySignature = signature;
            loadGames();
        }
    }

    private int contentDp() {
        Configuration cfg = getResources().getConfiguration();
        int w = cfg.screenWidthDp;
        int nav = w >= 840 ? 240 : w >= 600 ? 80 : 0;
        int inset = w >= 600 ? 24 : 16;
        return Math.max(200, w - nav - inset * 2);
    }

    private static int clamp(int v, int lo, int hi) {
        return Math.max(lo, Math.min(hi, v));
    }

    private void fill(LinearLayout container, List<View> cells, int cols, int gapDp) {
        container.removeAllViews();
        int gap = Ui.dp(requireContext(), gapDp);
        for (int i = 0; i < cells.size(); i += cols) {
            LinearLayout row = new LinearLayout(requireContext());
            row.setOrientation(LinearLayout.HORIZONTAL);
            row.setBaselineAligned(false);
            for (int k = 0; k < cols; k++) {
                View cell = i + k < cells.size() ? cells.get(i + k) : new Space(requireContext());
                ViewGroup.LayoutParams own = cell.getLayoutParams();
                int height = own != null && own.height > 0 ? own.height : ViewGroup.LayoutParams.WRAP_CONTENT;
                LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, height, 1f);
                if (k < cols - 1) lp.setMarginEnd(gap);
                row.addView(cell, lp);
            }
            LinearLayout.LayoutParams rp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
            if (i + cols < cells.size()) rp.bottomMargin = gap;
            container.addView(row, rp);
        }
    }

    private void loadNews() {
        newsDueAt = System.currentTimeMillis() + FEED_REFRESH_MS;
        Context app = requireContext().getApplicationContext();
        store.work.execute(() -> {
            List<HomeFeed.News> news = null;
            try {
                news = HomeFeed.latestNews(app, 2);
            } catch (IOException | JSONException | RuntimeException e) {
                android.util.Log.w("Voidstrap", "News feed failed", e);
            }
            List<HomeFeed.News> n = news;
            store.main.post(() -> bindNews(n));
        });
    }

    private void loadCatalog() {
        catalogDueAt = System.currentTimeMillis() + FEED_REFRESH_MS;
        Context app = requireContext().getApplicationContext();
        if (catalogGrid.getChildCount() == 0) {
            catalogStatus.setVisibility(View.VISIBLE);
            catalogStatus.setText(R.string.home_catalog_loading);
        }
        store.work.execute(() -> {
            List<HomeFeed.Item> items = null;
            try {
                items = HomeFeed.catalog(app);
            } catch (IOException | JSONException | RuntimeException e) {
                android.util.Log.w("Voidstrap", "Catalog feed failed", e);
            }
            List<HomeFeed.Item> it = items;
            store.main.post(() -> bindCatalog(it));
        });
    }

    private void bindNews(List<HomeFeed.News> news) {
        if (getView() == null) return;
        if (news == null) newsDueAt = System.currentTimeMillis() + FEED_RETRY_MS;
        if (news == null && newsGrid.getChildCount() > 0) return;
        boolean has = news != null && !news.isEmpty();
        newsHeader.setVisibility(has ? View.VISIBLE : View.GONE);
        newsGrid.setVisibility(has ? View.VISIBLE : View.GONE);
        if (!has) return;
        int recent = 0;
        for (HomeFeed.News n : news) if (n.created > 0 && System.currentTimeMillis() - n.created < 7 * DateUtils.DAY_IN_MILLIS) recent++;
        android.text.SpannableStringBuilder title = new android.text.SpannableStringBuilder(getString(R.string.home_news_title));
        if (recent > 0) {
            int start = title.length();
            title.append("  ").append(getResources().getQuantityString(R.plurals.home_news_count, recent, recent));
            title.setSpan(new android.text.style.AbsoluteSizeSpan(13, true), start, title.length(), android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
            title.setSpan(new android.text.style.ForegroundColorSpan(Ui.attr(requireContext(), R.attr.vsTextTertiary)), start, title.length(), android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
            title.setSpan(new android.text.style.StyleSpan(android.graphics.Typeface.NORMAL), start, title.length(), android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
        }
        newsCount.setText(title);
        List<View> cells = new ArrayList<>();
        LayoutInflater inf = LayoutInflater.from(requireContext());
        for (HomeFeed.News n : news) {
            View card = inf.inflate(R.layout.item_news_card, newsGrid, false);
            ((TextView) card.findViewById(R.id.news_title)).setText(HomeFeed.cardTitle(n.title));
            String date = newsDate(n.created);
            ((TextView) card.findViewById(R.id.news_date)).setText(date);
            View pill = card.findViewById(R.id.news_tag_pill);
            pill.setVisibility(n.tag.isEmpty() ? View.GONE : View.VISIBLE);
            ((TextView) card.findViewById(R.id.news_tag)).setText(n.tag);
            ImageView image = card.findViewById(R.id.news_image);
            Net.image(image, HomeFeed.thumbnail(n.image, 760), R.color.vs_news_surface, Ui.dp(requireContext(), 380));
            card.setContentDescription(getString(R.string.home_news_description, n.title, date));
            card.setOnClickListener(x -> Ui.openWeb(requireContext(), n.url));
            cells.add(card);
        }
        fill(newsGrid, cells, contentDp() >= 560 ? 2 : 1, 14);
    }

    private String newsDate(long created) {
        if (created <= 0) return getString(R.string.home_news_announcement);
        Calendar then = Calendar.getInstance();
        then.setTimeInMillis(created);
        Calendar now = Calendar.getInstance();
        long days = (startOfDay(now) - startOfDay(then)) / DateUtils.DAY_IN_MILLIS;
        if (days <= 0) return getString(R.string.home_news_today);
        if (days == 1) return getString(R.string.home_news_yesterday);
        if (days < 7) return getResources().getQuantityString(R.plurals.home_news_days_ago, (int) days, (int) days);
        return new SimpleDateFormat("MMMM d, yyyy", Locale.getDefault()).format(new Date(created)).toLowerCase(Locale.getDefault());
    }

    private static long startOfDay(Calendar c) {
        Calendar d = (Calendar) c.clone();
        d.set(Calendar.HOUR_OF_DAY, 0);
        d.set(Calendar.MINUTE, 0);
        d.set(Calendar.SECOND, 0);
        d.set(Calendar.MILLISECOND, 0);
        return d.getTimeInMillis();
    }

    private void bindCatalog(List<HomeFeed.Item> items) {
        if (getView() == null) return;
        if (items == null) catalogDueAt = System.currentTimeMillis() + FEED_RETRY_MS;
        if (items == null || items.isEmpty()) {
            if (catalogGrid.getChildCount() > 0) return;
            catalogStatus.setVisibility(View.VISIBLE);
            catalogStatus.setText(items == null ? R.string.home_catalog_failed : R.string.home_catalog_empty);
            return;
        }
        catalogStatus.setVisibility(View.GONE);
        List<View> cells = new ArrayList<>();
        LayoutInflater inf = LayoutInflater.from(requireContext());
        for (HomeFeed.Item it : items) {
            View card = inf.inflate(R.layout.item_catalog_card, catalogGrid, false);
            ((TextView) card.findViewById(R.id.catalog_name)).setText(it.name);
            ((TextView) card.findViewById(R.id.catalog_creator)).setText(it.creator);
            String price = HomeFeed.price(it);
            ((TextView) card.findViewById(R.id.catalog_price)).setText(price == null ? getString(R.string.home_free) : price);
            card.findViewById(R.id.catalog_limited).setVisibility(it.limited ? View.VISIBLE : View.GONE);
            TextView type = card.findViewById(R.id.catalog_type);
            type.setText(it.type);
            type.setVisibility(it.type.isEmpty() ? View.GONE : View.VISIBLE);
            Net.image(card.findViewById(R.id.catalog_image), it.image, R.color.vs_subtle, Ui.dp(requireContext(), 150));
            card.setContentDescription(getString(R.string.home_catalog_description, it.name, it.creator, price == null ? getString(R.string.home_free) : price));
            card.setOnClickListener(x -> Actions.openInRoblox(host(), it.appLink(), it.link()));
            cells.add(card);
        }
        fill(catalogGrid, cells, clamp(contentDp() / 150, 2, 5), 8);
    }

    private void loadGames() {
        List<Store.Launch> history = new ArrayList<>(store.history);
        bindGames(HomeFeed.base(history));
        int seq = ++gamesSeq;
        Context app = requireContext().getApplicationContext();
        store.work.execute(() -> {
            List<HomeFeed.GameCard> cards = HomeFeed.enrich(app, HomeFeed.base(history));
            store.main.post(() -> {
                if (seq == gamesSeq) bindGames(cards);
            });
        });
    }

    private void bindGames(List<HomeFeed.GameCard> games) {
        if (getView() == null) return;
        continueEmpty.setVisibility(games.isEmpty() ? View.VISIBLE : View.GONE);
        continueGrid.setVisibility(games.isEmpty() ? View.GONE : View.VISIBLE);
        List<View> cells = new ArrayList<>();
        LayoutInflater inf = LayoutInflater.from(requireContext());
        DateFormat when = DateFormat.getDateTimeInstance(DateFormat.MEDIUM, DateFormat.SHORT);
        for (HomeFeed.GameCard g : games) {
            View card = inf.inflate(R.layout.item_game_card, continueGrid, false);
            ((TextView) card.findViewById(R.id.game_name)).setText(g.name);
            TextView creator = card.findViewById(R.id.game_creator);
            creator.setText(g.creator);
            creator.setVisibility(g.creator == null || g.creator.isEmpty() ? View.GONE : View.VISIBLE);
            ((TextView) card.findViewById(R.id.game_likes)).setText(g.likes);
            ((TextView) card.findViewById(R.id.game_players)).setText(g.players);
            ((TextView) card.findViewById(R.id.game_launched)).setText(getString(R.string.home_launched, when.format(new Date(g.launched))));
            Net.image(card.findViewById(R.id.game_thumb), g.thumbnail, R.color.vs_subtle_pressed, Ui.dp(requireContext(), 320));
            View thumb = card.findViewById(R.id.game_thumb_host);
            thumb.setContentDescription(getString(R.string.home_game_page, g.name));
            thumb.setOnClickListener(x -> Actions.openInRoblox(host(), "https://www.roblox.com/games/" + g.placeId));
            MaterialButton play = card.findViewById(R.id.game_play);
            play.setContentDescription(getString(R.string.home_play, g.name));
            play.setOnClickListener(x -> Actions.launch(host(), Deeplink.place(g.placeId, null, null), g.name));
            card.findViewById(R.id.game_copy).setOnClickListener(x -> {
                Ui.copy(requireContext(), g.name, "https://www.roblox.com/games/" + g.placeId);
                Notify.say(host(), Notify.COPY, R.string.game_link_copied);
            });
            cells.add(card);
        }
        fill(continueGrid, cells, clamp(contentDp() / 260, 1, 3), 10);
    }

    private void exploreNews() {
        LinearLayout list = new LinearLayout(requireContext());
        list.setOrientation(LinearLayout.VERTICAL);
        TextView loading = new TextView(requireContext());
        loading.setText(R.string.home_news_loading);
        loading.setTextAppearance(R.style.TextAppearance_Voidstrap_Caption);
        list.addView(loading);
        MaterialButton all = new MaterialButton(requireContext(), null, com.google.android.material.R.attr.materialButtonOutlinedStyle);
        all.setText(R.string.home_news_open_forum);
        all.setIconResource(R.drawable.ic_globe);
        all.setOnClickListener(x -> Ui.openWeb(requireContext(), HomeFeed.NEWS_PAGE));
        Ui.surface(host(), R.string.home_news_all_title, R.string.home_news_all_body, list).show();
        Context app = requireContext().getApplicationContext();
        store.work.execute(() -> {
            List<HomeFeed.News> news = null;
            try {
                news = HomeFeed.allNews(app);
            } catch (IOException | JSONException | RuntimeException ignored) {
            }
            List<HomeFeed.News> n = news;
            store.main.post(() -> {
                if (!isAdded()) return;
                list.removeAllViews();
                if (n == null || n.isEmpty()) {
                    loading.setText(R.string.home_news_failed);
                    list.addView(loading);
                }
                if (n != null) for (HomeFeed.News item : n) {
                    Row row = Row.inflate(list);
                    String detail = item.tag.isEmpty() ? newsDate(item.created) : getString(R.string.pair, item.tag, newsDate(item.created));
                    row.set(R.drawable.ic_megaphone, item.title, detail);
                    if (!item.image.isEmpty()) row.showImage(HomeFeed.thumbnail(item.image, 160), R.drawable.ic_megaphone);
                    row.title.setMaxLines(2);
                    row.chevron.setVisibility(View.VISIBLE);
                    row.view.setOnClickListener(x -> Ui.openWeb(requireContext(), item.url));
                    list.addView(row.view);
                }
                LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
                lp.topMargin = Ui.dp(requireContext(), 12);
                list.addView(all, lp);
            });
        });
    }
}
