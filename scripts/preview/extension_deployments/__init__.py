"""Trusted deployment recipes; workload catalogues never choose executable modules."""
from . import postgresql

RECIPES = {'postgresql': postgresql}
DEPENDENCY_NAMES = {recipe.SERVICE for recipe in RECIPES.values()}


def labels(names):
    import preview
    selected, options = [], {}
    for name in names:
        if not name.startswith('preview:extension:'):
            continue
        identifier = name[len('preview:extension:'):]
        if ':' in identifier:
            identifier, modifier = identifier.split(':', 1)
            preview.require(identifier in RECIPES, 'Unknown extension deployment modifier.')
            preview.require(identifier not in options, 'Duplicate extension deployment modifier.')
            options[identifier] = RECIPES[identifier].modifier(modifier)
        else:
            selected.append(identifier)
    preview.require(set(options) <= set(selected), 'An extension modifier requires its extension selection label.')
    return sorted(selected), options


def freeze(selected, options=None):
    import preview
    options = {} if options is None else options
    preview.require(isinstance(options, dict) and set(options) <= set(selected) & set(RECIPES),
                    'Unexpected extension deployment options.')
    return {name: RECIPES[name].freeze(options.get(name, {})) for name in sorted(selected) if name in RECIPES}


def validate(plans, selected):
    import preview
    preview.require(isinstance(plans, dict) and set(plans) == set(selected) & set(RECIPES),
                    'Deployment recipes must match selected extensions.')
    for name, plan in plans.items():
        RECIPES[name].validate(plan)
    return plans


def images(plans):
    result = {}
    for name, plan in plans.items():
        result.update(RECIPES[name].images(plan))
    return result


def compatible(plans, deliveries):
    for name, plan in plans.items():
        RECIPES[name].compatible(plan, deliveries[name])


def intent(plans):
    return {name: {key: value for key, value in plan.items() if key != 'image'} for name, plan in plans.items()}


def describe(plans):
    return [RECIPES[name].describe(plan) for name, plan in plans.items()]


def preprovisioned_files(plans):
    return {file for name, plan in plans.items() for file in RECIPES[name].preprovisioned_files(plan)}


def evidence(directory, state):
    return {name: RECIPES[name].evidence(directory, plan) for name, plan in state.get('extensionDeployments', {}).items()}


def qualify(request, registry, root):
    return {name: RECIPES[name].qualify(request, registry, root) for name in request.get('extensionDeployments', {})}


def prepare(directory, state, settings):
    import copy
    settings = copy.deepcopy(settings)
    history = state.setdefault('extensionDeploymentHistory', {}) if state.get('extensionDeployments') else state.get('extensionDeploymentHistory', {})
    for name, plan in state.get('extensionDeployments', {}).items():
        RECIPES[name].prepare(directory, state, settings.setdefault(name, {}), plan, history.get(name))
        history[name] = plan
    return settings


def merge(target, fragment):
    for key, value in fragment.items():
        if isinstance(value, dict):
            merge(target.setdefault(key, {}), value)
        elif key in ('secrets', 'volumes') and isinstance(value, list) and key in target:
            target[key].extend(item for item in value if item not in target[key])
        else:
            target[key] = value
    return target


def overlay(directory, state):
    result = {'services': {}, 'volumes': {}, 'secrets': {}}
    for name, plan in state.get('extensionDeployments', {}).items():
        fragment = RECIPES[name].overlay(directory, state, plan)
        merge(result, fragment)
    return result


def reset(directory, state):
    for name in state.get('extensionDeploymentHistory', {}):
        RECIPES[name].reset(directory)
    if 'extensionDeploymentHistory' in state:
        state['extensionDeploymentHistory'] = {}
    import extension_tls
    extension_tls.reset(directory, state)


def require_preserving_change(previous, requested):
    before = previous.get('extensionDeployments', {})
    after = requested.get('extensionDeployments', {})
    for name in set(before) & set(after):
        RECIPES[name].require_preserving_change(before[name], after[name])
