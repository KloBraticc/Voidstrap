#include <windows.h>
#include <reshade.hpp>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>

using namespace reshade::api;

extern "C" __declspec(dllexport) const char *NAME = "MuMu Depth";
extern "C" __declspec(dllexport) const char *DESCRIPTION = "Copies the game depth buffer from MuMu's guest Vulkan device to the device that presents the window.";

namespace
{
    struct depth_stats
    {
        uint32_t draws = 0;
        uint32_t last_frame = 0;
    };

    struct source_state
    {
        std::unordered_map<uint64_t, depth_stats> stats;
        uint64_t best = 0;
        resource shared = {};
        resource_desc shared_desc = {};
        bool shared_readable = false;
    };

    struct command_state
    {
        uint64_t dsv_resource = 0;
    };

    struct presenter_state
    {
        resource imported = {};
        resource_view srv = {};
        uint64_t generation = 0;
    };

    std::mutex lock;
    std::unordered_set<device *> presenters;
    std::unordered_map<device *, source_state> sources;
    std::unordered_map<command_list *, command_state> commands;
    std::unordered_map<effect_runtime *, presenter_state> runtimes;

    HANDLE shared_handle = nullptr;
    resource_desc published_desc = {};
    uint64_t generation = 0;

    void log(const std::string &text)
    {
        reshade::log::message(reshade::log::level::info, text.c_str());
    }

    format view_format(format f)
    {
        switch (f)
        {
        case format::d16_unorm:
            return format::r16_unorm;
        case format::d24_unorm_s8_uint:
        case format::d24_unorm_x8_uint:
            return format::r24_unorm_x8_uint;
        case format::d32_float:
            return format::r32_float;
        case format::d32_float_s8_uint:
            return format::r32_float_x8_uint;
        default:
            return format_to_default_typed(f);
        }
    }

    bool is_depth(format f)
    {
        return f == format::d16_unorm || f == format::d24_unorm_s8_uint || f == format::d24_unorm_x8_uint ||
               f == format::d32_float || f == format::d32_float_s8_uint;
    }

    void release_shared(device *dev, source_state &s)
    {
        if (s.shared.handle != 0)
            dev->destroy_resource(s.shared);
        s.shared = {};
        s.shared_readable = false;
    }

    bool ensure_shared(device *dev, source_state &s, const resource_desc &src)
    {
        if (s.shared.handle != 0 && s.shared_desc.texture.width == src.texture.width &&
            s.shared_desc.texture.height == src.texture.height && s.shared_desc.texture.format == src.texture.format)
            return true;
        release_shared(dev, s);
        if (!dev->check_capability(device_caps::shared_resource_nt_handle))
            return false;
        resource_desc desc(src.texture.width, src.texture.height, 1, 1, src.texture.format, 1, memory_heap::default_,
                           resource_usage::copy_dest | resource_usage::shader_resource,
                           resource_flags::shared | resource_flags::shared_nt_handle);
        HANDLE handle = nullptr;
        if (!dev->create_resource(desc, nullptr, resource_usage::copy_dest, &s.shared, &handle) || handle == nullptr)
        {
            log("could not create shared depth image");
            s.shared = {};
            return false;
        }
        s.shared_desc = desc;
        shared_handle = handle;
        published_desc = desc;
        generation++;
        log("shared depth image " + std::to_string(desc.texture.width) + "x" + std::to_string(desc.texture.height) +
            " format " + std::to_string(static_cast<uint32_t>(desc.texture.format)));
        return true;
    }

    void copy_depth(command_list *cmd, device *dev, source_state &s, resource src)
    {
        const resource_desc desc = dev->get_resource_desc(src);
        if (!ensure_shared(dev, s, desc))
            return;
        const resource_usage shared_from = s.shared_readable ? resource_usage::shader_resource : resource_usage::copy_dest;
        resource res[2] = { src, s.shared };
        resource_usage from[2] = { resource_usage::depth_stencil_write, shared_from };
        resource_usage to[2] = { resource_usage::copy_source, resource_usage::copy_dest };
        cmd->barrier(2, res, from, to);
        cmd->copy_resource(src, s.shared);
        resource_usage back_from[2] = { resource_usage::copy_source, resource_usage::copy_dest };
        resource_usage back_to[2] = { resource_usage::depth_stencil_write, resource_usage::shader_resource };
        cmd->barrier(2, res, back_from, back_to);
        s.shared_readable = true;
    }

    void on_depth_cleared(command_list *cmd, resource_view dsv)
    {
        device *dev = cmd->get_device();
        std::lock_guard guard(lock);
        if (presenters.contains(dev))
            return;
        source_state &s = sources[dev];
        const resource res = dev->get_resource_from_view(dsv);
        if (res.handle == 0)
            return;
        depth_stats &st = s.stats[res.handle];
        st.last_frame = st.draws;
        st.draws = 0;
        uint32_t most = 0;
        for (const auto &[handle, value] : s.stats)
        {
            if (value.last_frame > most)
            {
                most = value.last_frame;
                s.best = handle;
            }
        }
        if (res.handle == s.best && st.last_frame > 0)
            copy_depth(cmd, dev, s, res);
    }

    void on_init_swapchain(swapchain *chain, bool)
    {
        std::lock_guard guard(lock);
        presenters.insert(chain->get_device());
    }

    void on_destroy_device(device *dev)
    {
        std::lock_guard guard(lock);
        auto it = sources.find(dev);
        if (it != sources.end())
        {
            release_shared(dev, it->second);
            sources.erase(it);
        }
        presenters.erase(dev);
    }

    void on_destroy_command_list(command_list *cmd)
    {
        std::lock_guard guard(lock);
        commands.erase(cmd);
    }

    void set_dsv(command_list *cmd, resource_view dsv)
    {
        device *dev = cmd->get_device();
        std::lock_guard guard(lock);
        commands[cmd].dsv_resource = dsv.handle != 0 ? dev->get_resource_from_view(dsv).handle : 0;
    }

    bool on_begin_render_pass(command_list *cmd, uint32_t, const render_pass_render_target_desc *,
                              const render_pass_depth_stencil_desc *ds, render_pass_flags)
    {
        if (ds != nullptr && ds->view.handle != 0)
        {
            const resource_desc desc = cmd->get_device()->get_resource_desc(cmd->get_device()->get_resource_from_view(ds->view));
            if (is_depth(desc.texture.format) && ds->depth_load_op == render_pass_load_op::clear)
                on_depth_cleared(cmd, ds->view);
            set_dsv(cmd, is_depth(desc.texture.format) ? ds->view : resource_view{});
        }
        else
        {
            set_dsv(cmd, {});
        }
        return false;
    }

    bool on_end_render_pass(command_list *cmd)
    {
        set_dsv(cmd, {});
        return false;
    }

    void on_bind_targets(command_list *cmd, uint32_t, const resource_view *, resource_view dsv)
    {
        set_dsv(cmd, dsv);
    }

    bool on_clear_dsv(command_list *cmd, resource_view dsv, const float *depth, const uint8_t *, uint32_t, const rect *)
    {
        if (depth != nullptr)
            on_depth_cleared(cmd, dsv);
        return false;
    }

    void count_draw(command_list *cmd)
    {
        device *dev = cmd->get_device();
        std::lock_guard guard(lock);
        if (presenters.contains(dev))
            return;
        auto it = commands.find(cmd);
        if (it == commands.end() || it->second.dsv_resource == 0)
            return;
        sources[dev].stats[it->second.dsv_resource].draws++;
    }

    bool on_draw(command_list *cmd, uint32_t, uint32_t, uint32_t, uint32_t)
    {
        count_draw(cmd);
        return false;
    }

    bool on_draw_indexed(command_list *cmd, uint32_t, uint32_t, uint32_t, int32_t, uint32_t)
    {
        count_draw(cmd);
        return false;
    }

    void release_import(effect_runtime *runtime, presenter_state &p)
    {
        device *dev = runtime->get_device();
        if (p.srv.handle != 0)
            dev->destroy_resource_view(p.srv);
        if (p.imported.handle != 0)
            dev->destroy_resource(p.imported);
        p = {};
    }

    void on_begin_effects(effect_runtime *runtime, command_list *, resource_view, resource_view)
    {
        std::lock_guard guard(lock);
        if (shared_handle == nullptr)
            return;
        presenter_state &p = runtimes[runtime];
        if (p.generation == generation)
            return;
        release_import(runtime, p);
        device *dev = runtime->get_device();
        HANDLE handle = shared_handle;
        if (!dev->create_resource(published_desc, nullptr, resource_usage::shader_resource, &p.imported, &handle))
        {
            log("could not import shared depth image on the presenting device");
            p.generation = generation;
            return;
        }
        if (!dev->create_resource_view(p.imported, resource_usage::shader_resource,
                                       resource_view_desc(view_format(published_desc.texture.format)), &p.srv))
        {
            log("could not create depth view on the presenting device");
            release_import(runtime, p);
            p.generation = generation;
            return;
        }
        p.generation = generation;
        runtime->update_texture_bindings("DEPTH", p.srv, p.srv);
        log("depth bound to effects");
    }

    void on_destroy_runtime(effect_runtime *runtime)
    {
        std::lock_guard guard(lock);
        auto it = runtimes.find(runtime);
        if (it == runtimes.end())
            return;
        release_import(runtime, it->second);
        runtimes.erase(it);
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        if (!reshade::register_addon(module))
            return FALSE;
        reshade::register_event<reshade::addon_event::init_swapchain>(on_init_swapchain);
        reshade::register_event<reshade::addon_event::destroy_device>(on_destroy_device);
        reshade::register_event<reshade::addon_event::destroy_command_list>(on_destroy_command_list);
        reshade::register_event<reshade::addon_event::begin_render_pass>(on_begin_render_pass);
        reshade::register_event<reshade::addon_event::end_render_pass>(on_end_render_pass);
        reshade::register_event<reshade::addon_event::bind_render_targets_and_depth_stencil>(on_bind_targets);
        reshade::register_event<reshade::addon_event::clear_depth_stencil_view>(on_clear_dsv);
        reshade::register_event<reshade::addon_event::draw>(on_draw);
        reshade::register_event<reshade::addon_event::draw_indexed>(on_draw_indexed);
        reshade::register_event<reshade::addon_event::reshade_begin_effects>(on_begin_effects);
        reshade::register_event<reshade::addon_event::destroy_effect_runtime>(on_destroy_runtime);
        break;
    case DLL_PROCESS_DETACH:
        reshade::unregister_addon(module);
        break;
    }
    return TRUE;
}
